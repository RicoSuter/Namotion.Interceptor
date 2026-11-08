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
internal sealed class EventTriggerEngine(SubjectPathResolver resolver, TimeProvider timeProvider, AutomationInputQueue queue)
{
    /// <summary>
    /// Resolves the source of every trigger of the active rules, recording the routes with <paramref name="route"/>, and
    /// subscribes the triggers that are not bound yet and those whose path leads to another subject now; a trigger that a
    /// Save carried over keeps its subscription while its path still leads to its source.
    /// </summary>
    public void Bind(IReadOnlyList<CompiledRule> rules, ISubjectPathRecorder route)
    {
        foreach (var rule in rules)
        {
            if (!rule.IsActivated)
            {
                continue;
            }

            foreach (var trigger in rule.EventTriggers)
            {
                var source = AutomationPaths.ResolveSubject(resolver, trigger.Configuration.SubjectPath, rule.Rule, route);
                if (trigger.IsBound && ReferenceEquals(source, trigger.Source))
                {
                    continue;
                }

                Unsubscribe(trigger);
                Subscribe(trigger, source);
            }
        }
    }

    public static void Deactivate(CompiledRule rule)
    {
        foreach (var trigger in rule.EventTriggers)
        {
            Unsubscribe(trigger);
            trigger.IsBound = false;
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

    /// <summary>Resolves the source of the trigger and the type of its event; returns why the trigger receives no events, or null.</summary>
    public static string? FindSource(SubjectPathResolver resolver, CompiledEventTrigger trigger, out IInterceptorSubject? source, out Type? eventType)
    {
        source = AutomationPaths.ResolveSubject(resolver, trigger.Configuration.SubjectPath, trigger.Rule.Rule);
        return CheckSource(trigger.Configuration, source, out eventType);
    }

    private static string? CheckSource(SubjectEventTrigger configuration, IInterceptorSubject? source, out Type? eventType)
    {
        eventType = null;
        if (source is null)
        {
            return $"No subject at {configuration.SubjectPath}";
        }

        eventType = EventSources.FindEventType(source.GetType(), configuration.EventType);
        return eventType is null ? $"{configuration.SubjectPath} publishes no {configuration.EventType}" : null;
    }

    private void Subscribe(CompiledEventTrigger trigger, IInterceptorSubject? source)
    {
        var error = CheckSource(trigger.Configuration, source, out var eventType);
        trigger.Source = source;
        trigger.SourceError = error;
        trigger.IsBound = true;
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
