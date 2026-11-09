using HomeBlaze.Automation.Events;
using HomeBlaze.Automation.Expressions;
using HomeBlaze.Services;
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
    /// the source of the subscription that a Save carried over. Does nothing for a rule that listens already.
    /// </summary>
    public void Listen(CompiledRule rule)
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
                Join(trigger);
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

    /// <summary>Subscribes the triggers of the listening rules again whose path now leads to another subject.</summary>
    public void Rebind(IReadOnlyList<CompiledRule> rules)
    {
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
                    Join(trigger);
                }
            }
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
        AutomationPaths.ResolveSubject(resolver, trigger.Configuration.SubjectPath, trigger.Rule.Rule);

    /// <summary>Resolves the source of the trigger and the type of its event; returns why the trigger receives no events, or null.</summary>
    public static string? FindSource(SubjectPathResolver resolver, CompiledEventTrigger trigger, out IInterceptorSubject? source, out Type? eventType)
    {
        var configuration = trigger.Configuration;
        eventType = null;
        source = AutomationPaths.ResolveSubject(resolver, configuration.SubjectPath, trigger.Rule.Rule);
        if (source is null)
        {
            return $"No subject at {configuration.SubjectPath}";
        }

        eventType = EventSources.FindEventType(source.GetType(), configuration.EventType);
        return eventType is null ? $"{configuration.SubjectPath} publishes no {configuration.EventType}" : null;
    }

    private void Join(CompiledEventTrigger trigger)
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
            trigger.JoinedAfter = shared.Received;
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

    private static void Use(CompiledEventTrigger trigger, EventSubscription subscription)
    {
        subscription.TriggerCount++;
        trigger.Subscription = subscription;
        trigger.SourceError = null;
    }

    private EventSubscription? Subscribe(CompiledEventTrigger trigger, IInterceptorSubject source, Type eventType)
    {
        var subscription = new EventSubscription(source, eventType);
        try
        {
            subscription.Handle = EventSources.Subscribe(source, eventType, payload => queue.TryEnqueue(
                new EventInput(subscription, new CapturedEvent(source, payload), subscription.Receive(), AutomationInputQueue.CausedChainDepth)));
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

    // The last trigger that leaves a subscription disposes it; an event it queued before finds no trigger and is dropped.
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

        subscription.Dispose();
        var subscriptions = _subscriptions[subscription.Source];
        subscriptions.Remove(subscription);
        if (subscriptions.Count == 0)
        {
            _subscriptions.Remove(subscription.Source);
        }
    }
}
