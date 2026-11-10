using HomeBlaze.Abstractions.Messaging;
using HomeBlaze.Automation.Expressions;
using HomeBlaze.Services;
using HomeBlaze.Services.Messaging;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Expressions;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// The event triggers of the rules that listen for events: shares one subscription per source and event type among
/// them, follows a source that the graph replaces, and turns a received event into the run requests of the rules that are
/// active when the worker processes it, in rule order, whose filters are checked when each rule is about to run. Used by
/// the worker only; a subscription only enqueues.
/// </summary>
internal sealed class EventTriggerEngine(
    SubjectPathResolver resolver, AutomationInputQueue queue, ILogger logger, Func<string> getAutomationName)
{
    private readonly Dictionary<IInterceptorSubject, List<EventSubscription>> _subscriptions = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Has the rule listen for events: subscribes each trigger that has no subscription, or whose path no longer leads to
    /// the source of the subscription that a Save carried over, to the events after <paramref name="joinedAfter"/>. Does
    /// nothing for a rule that listens already.
    /// </summary>
    public void Listen(CompiledRule rule, long joinedAfter)
    {
        if (rule.ListensForEvents)
        {
            return;
        }

        rule.ListensForEvents = true;
        foreach (var trigger in rule.EventTriggers)
        {
            if (trigger.Subscription is null || !ReferenceEquals(ResolveSource(trigger), trigger.Source))
            {
                Leave(trigger);
                Join(trigger, joinedAfter);
            }
        }
    }

    public void StopListening(CompiledRule rule)
    {
        rule.ListensForEvents = false;
        foreach (var trigger in rule.EventTriggers)
        {
            Leave(trigger);
        }
    }

    /// <summary>
    /// Subscribes the triggers of the listening rules again whose path now leads to another subject, and then records on
    /// each subscription whether an active rule uses it, see <see cref="RefreshActive"/>.
    /// </summary>
    public void Rebind(IReadOnlyList<CompiledRule> rules)
    {
        var joinedAfter = queue.LastEventSequence;
        var isRebound = false;
        foreach (var rule in rules)
        {
            if (!rule.ListensForEvents)
            {
                continue;
            }

            foreach (var trigger in rule.EventTriggers)
            {
                if (!ReferenceEquals(ResolveSource(trigger), trigger.Source))
                {
                    Leave(trigger);
                    Join(trigger, joinedAfter);
                    isRebound = true;
                }
            }
        }

        // A subscription that an active trigger left keeps the flag that the trigger set.
        if (isRebound)
        {
            RefreshActive(rules);
        }
    }

    /// <summary>
    /// Gets the run requests of the event, in rule order: one for each active rule with a trigger that uses the
    /// subscription of the event and received the event after it joined. The filters are checked by
    /// <see cref="CheckFilters"/> just before each rule runs, so that they see what the earlier rules changed.
    /// </summary>
    public static IReadOnlyList<RunRequest> OnEvent(EventInput input, IReadOnlyList<CompiledRule> rules)
    {
        List<RunRequest>? requests = null;
        foreach (var rule in rules)
        {
            if (rule.IsActivated && FindReceivingTrigger(rule, input) is { } trigger)
            {
                requests ??= [];
                requests.Add(new RunRequest(rule, input.Event, trigger.Description, EventInput: input));
            }
        }

        return requests ?? (IReadOnlyList<RunRequest>)[];
    }

    /// <summary>
    /// Checks the filters of the triggers of an event request against the graph now: returns the request with the first
    /// trigger whose filter passes as its source, or null when none passes.
    /// </summary>
    public static RunRequest? CheckFilters(SubjectPathResolver resolver, TimeProvider timeProvider, RunRequest request)
    {
        var input = request.EventInput!;
        var isFirst = true;
        foreach (var trigger in request.Rule.EventTriggers)
        {
            if (!Receives(trigger, input))
            {
                continue;
            }

            if (trigger.Filter is not { } filter || Passes(resolver, timeProvider, trigger, filter, input.Event))
            {
                return isFirst ? request : request with { Source = trigger.Description };
            }

            isFirst = false;
        }

        return null;
    }

    private static CompiledEventTrigger? FindReceivingTrigger(CompiledRule rule, EventInput input)
    {
        foreach (var trigger in rule.EventTriggers)
        {
            if (Receives(trigger, input))
            {
                return trigger;
            }
        }

        return null;
    }

    private static bool Receives(CompiledEventTrigger trigger, EventInput input) =>
        ReferenceEquals(trigger.Subscription, input.Subscription) && input.Sequence > trigger.JoinedAfter;

    private static bool Passes(SubjectPathResolver resolver, TimeProvider timeProvider, CompiledEventTrigger trigger, ParsedExpression filter, CapturedEvent captured)
    {
        var evaluation = new AutomationEvaluation(resolver, trigger.Rule.Rule, timeProvider.GetUtcNow(), timeProvider.LocalTimeZone, captured);
        return evaluation.EvaluateCondition(filter) == true;
    }

    private IInterceptorSubject? ResolveSource(CompiledEventTrigger trigger) =>
        resolver.ResolveSubject(trigger.Configuration.SubjectPath, trigger.Rule.Rule);

    /// <summary>Resolves the source of the trigger and the type of its event; returns why the trigger receives no events, or null.</summary>
    public static string? FindSource(SubjectPathResolver resolver, CompiledEventTrigger trigger, out IInterceptorSubject? source, out Type? eventType)
    {
        var configuration = trigger.Configuration;
        eventType = null;
        source = resolver.ResolveSubject(configuration.SubjectPath, trigger.Rule.Rule);
        if (source is null)
        {
            return $"No subject at {configuration.SubjectPath}";
        }

        eventType = SubjectEvents.FindEventType(source.GetType(), configuration.EventType);
        return eventType is null ? $"{configuration.SubjectPath} publishes no {configuration.EventType}" : null;
    }

    private void Join(CompiledEventTrigger trigger, long joinedAfter)
    {
        var error = FindSource(resolver, trigger, out var source, out var eventType);
        trigger.Source = source;
        if (source is null || eventType is null)
        {
            trigger.SourceError = error;
            return;
        }

        if (Find(source, eventType) is { } shared)
        {
            trigger.JoinedAfter = joinedAfter;
            Use(trigger, shared);
            return;
        }

        // An event that the source publishes while it is subscribed is a new one, so the trigger handles every event.
        trigger.JoinedAfter = 0;
        if (Subscribe(trigger, source, eventType) is { } subscription)
        {
            Use(trigger, subscription);
        }
    }

    /// <summary>
    /// Records on each subscription whether an active rule uses it, so that an event beyond the chain limit that no
    /// active rule handles is not reported as a stopped feedback chain.
    /// </summary>
    public void RefreshActive(IReadOnlyList<CompiledRule> rules)
    {
        foreach (var rule in rules)
        {
            if (!rule.IsActivated)
            {
                continue;
            }

            foreach (var trigger in rule.EventTriggers)
            {
                if (trigger.Subscription is { } subscription)
                {
                    subscription.IsUsedByActiveRule = true;
                }
            }
        }

        foreach (var subscriptions in _subscriptions.Values)
        {
            foreach (var subscription in subscriptions)
            {
                subscription.HasActiveTrigger = subscription.IsUsedByActiveRule;
                subscription.IsUsedByActiveRule = false;
            }
        }
    }

    private static void Use(CompiledEventTrigger trigger, EventSubscription subscription)
    {
        subscription.HasActiveTrigger |= trigger.Rule.IsActivated;
        subscription.TriggerCount++;
        trigger.Subscription = subscription;
        trigger.SourceError = null;
    }

    private EventSubscription? Subscribe(CompiledEventTrigger trigger, IInterceptorSubject source, Type eventType)
    {
        var subscription = new EventSubscription(source, eventType);
        try
        {
            subscription.Handle = SubjectEvents.Subscribe(source, eventType, payload => Receive(subscription, payload));
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "The automation {Automation} could not subscribe to the {EventType} of {Path}.",
                getAutomationName(), trigger.Configuration.EventType, trigger.Configuration.SubjectPath);
            trigger.SourceError = $"{trigger.Configuration.SubjectPath} refused the subscription to {trigger.Configuration.EventType}: {exception.Message}";
            return null;
        }

        if (!_subscriptions.TryGetValue(source, out var subscriptions))
        {
            subscriptions = [];
            _subscriptions.Add(source, subscriptions);
        }

        subscriptions.Add(subscription);
        return subscription;
    }

    // Called on the thread that publishes the event. An event beyond the chain limit that no active rule handles would
    // continue no chain, so it is dropped without being counted as one.
    private void Receive(EventSubscription subscription, IEvent payload)
    {
        var chainDepth = AutomationInputQueue.CausedChainDepth;
        if (chainDepth > AutomationInputQueue.MaxChainDepth && !subscription.HasActiveTrigger)
        {
            return;
        }

        queue.TryEnqueue(new EventInput(subscription, new CapturedEvent(subscription.Source, payload), queue.NextEventSequence(), chainDepth));
    }

    private EventSubscription? Find(IInterceptorSubject source, Type eventType)
    {
        if (_subscriptions.TryGetValue(source, out var subscriptions))
        {
            foreach (var subscription in subscriptions)
            {
                if (subscription.EventType == eventType)
                {
                    return subscription;
                }
            }
        }

        return null;
    }

    // The last trigger that leaves a subscription disposes it; an event it queued or delivers later finds no trigger and
    // is dropped. It is removed first, so that a source that fails to end it leaves no subscription for a trigger to join.
    private void Leave(CompiledEventTrigger trigger)
    {
        if (trigger.Subscription is not { } subscription)
        {
            return;
        }

        trigger.Subscription = null;
        subscription.TriggerCount--;
        if (subscription.TriggerCount > 0)
        {
            return;
        }

        var subscriptions = _subscriptions[subscription.Source];
        subscriptions.Remove(subscription);
        if (subscriptions.Count == 0)
        {
            _subscriptions.Remove(subscription.Source);
        }

        // A source can still deliver while or after the subscription ends.
        subscription.HasActiveTrigger = false;
        try
        {
            subscription.Dispose();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "The automation {Automation} could not end its subscription to the {EventType} of {Path}.",
                getAutomationName(), trigger.Configuration.EventType, trigger.Configuration.SubjectPath);
        }
    }
}
