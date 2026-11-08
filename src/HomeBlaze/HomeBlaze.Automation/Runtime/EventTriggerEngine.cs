using HomeBlaze.Automation.Events;
using HomeBlaze.Automation.Expressions;
using HomeBlaze.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Expressions;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// The event triggers of active rules: subscribes to their sources, follows a source that the graph replaces, and turns
/// a received event that passes its filter into a run request. Used by the worker only; a subscription only enqueues.
/// </summary>
internal sealed class EventTriggerEngine(SubjectPathResolver resolver, TimeProvider timeProvider, AutomationInputQueue queue, GraphRoutes? routes = null)
{
    /// <summary>Subscribes the triggers of the rule, except one that a Save carried over and whose path still leads to its source.</summary>
    public void Activate(CompiledRule rule)
    {
        foreach (var trigger in rule.EventTriggers)
        {
            if (trigger.Subscription is not null && ReferenceEquals(ResolveSource(trigger), trigger.Source))
            {
                continue;
            }

            Unsubscribe(trigger);
            Subscribe(trigger);
        }
    }

    public static void Deactivate(CompiledRule rule)
    {
        foreach (var trigger in rule.EventTriggers)
        {
            Unsubscribe(trigger);
            trigger.SourceRoute.Clear();
        }
    }

    /// <summary>Subscribes the triggers of the active rules again whose path now leads to another subject.</summary>
    public void Rebind(IReadOnlyList<CompiledRule> rules)
    {
        foreach (var trigger in rules.Where(rule => rule.IsActivated).SelectMany(rule => rule.EventTriggers))
        {
            if (!ReferenceEquals(ResolveSource(trigger), trigger.Source))
            {
                Unsubscribe(trigger);
                Subscribe(trigger);
            }
        }
    }

    public IReadOnlyList<RunRequest> OnEvent(EventInput input)
    {
        var trigger = input.Subscription.Trigger;
        if (!ReferenceEquals(trigger.Subscription, input.Subscription) || !trigger.Rule.IsActivated)
        {
            return [];
        }

        if (trigger.Filter is { } filter && !Passes(trigger, filter, input.Event))
        {
            return [];
        }

        return [new RunRequest(trigger.Rule, input.Event, trigger.Description)];
    }

    private bool Passes(CompiledEventTrigger trigger, ParsedExpression filter, CapturedEvent captured)
    {
        var evaluation = new AutomationEvaluation(resolver, trigger.Rule.Rule, timeProvider.GetUtcNow(), timeProvider.LocalTimeZone, captured);
        return evaluation.EvaluateCondition(filter) == true;
    }

    // Stores the graph route of the resolution in the trigger.
    private IInterceptorSubject? ResolveSource(CompiledEventTrigger trigger)
    {
        var source = AutomationPaths.ResolveSubject(resolver, trigger.Configuration.SubjectPath, trigger.Rule.Rule, routes?.BeginRoute());
        routes?.EndRoute(trigger.SourceRoute);
        return source;
    }

    /// <summary>
    /// Resolves the source of the trigger and the type of its event, reporting the graph route of the source to
    /// <paramref name="recorder"/>; returns why the trigger receives no events, or null.
    /// </summary>
    public static string? FindSource(
        SubjectPathResolver resolver, CompiledEventTrigger trigger, out IInterceptorSubject? source, out Type? eventType, ISubjectPathRecorder? recorder = null)
    {
        var configuration = trigger.Configuration;
        eventType = null;
        source = AutomationPaths.ResolveSubject(resolver, configuration.SubjectPath, trigger.Rule.Rule, recorder);
        if (source is null)
        {
            return $"No subject at {configuration.SubjectPath}";
        }

        eventType = EventSources.FindEventType(source.GetType(), configuration.EventType);
        return eventType is null ? $"{configuration.SubjectPath} publishes no {configuration.EventType}" : null;
    }

    private void Subscribe(CompiledEventTrigger trigger)
    {
        var error = FindSource(resolver, trigger, out var source, out var eventType, routes?.BeginRoute());
        routes?.EndRoute(trigger.SourceRoute);
        trigger.Source = source;
        trigger.SourceError = error;
        if (source is null || eventType is null)
        {
            return;
        }

        // Assigned before the source is subscribed, so that an event it publishes at once is not taken for a stale one.
        var subscription = new EventTriggerSubscription(trigger);
        trigger.Subscription = subscription;
        subscription.Handle = EventSources.Subscribe(source, eventType, payload =>
            queue.TryEnqueue(new EventInput(subscription, new CapturedEvent(source, payload), AutomationInputQueue.CausedChainDepth)));
    }

    private static void Unsubscribe(CompiledEventTrigger trigger)
    {
        trigger.Subscription?.Dispose();
        trigger.Subscription = null;
    }
}
