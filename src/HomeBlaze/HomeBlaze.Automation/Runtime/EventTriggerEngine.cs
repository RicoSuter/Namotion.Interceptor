using HomeBlaze.Automation.Events;
using HomeBlaze.Automation.Expressions;
using HomeBlaze.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Expressions;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// The event triggers of active rules: subscribes to their sources, follows a source that the graph replaces, and turns
/// a received event that passes its filter into a run request. Resolves the path of each source with its graph route.
/// Used by the worker only; a subscription only enqueues.
/// </summary>
internal sealed class EventTriggerEngine(GraphRoutes routes, TimeProvider timeProvider, AutomationInputQueue queue)
{
    /// <summary>Subscribes the triggers of the rule, except one that a Save carried over and whose path still leads to its source.</summary>
    public void Activate(CompiledRule rule)
    {
        foreach (var trigger in rule.EventTriggers)
        {
            var source = ResolveSource(trigger);
            if (trigger.Subscription is not null && ReferenceEquals(source, trigger.Source))
            {
                continue;
            }

            Unsubscribe(trigger);
            Subscribe(trigger, source);
        }
    }

    public static void Deactivate(CompiledRule rule)
    {
        foreach (var trigger in rule.EventTriggers)
        {
            Unsubscribe(trigger);
            trigger.SourceRoute = GraphRoute.Empty;
            trigger.SourceRouteVersion = -1;
        }
    }

    /// <summary>
    /// Subscribes the triggers of the active rules again whose path now leads to another subject; with
    /// <paramref name="onlyChanged"/>, resolves only the paths that the graph changes taken last may lead elsewhere now,
    /// see <see cref="GraphRoutes.NeedsResolving"/>.
    /// </summary>
    public void Rebind(IReadOnlyList<CompiledRule> rules, bool onlyChanged)
    {
        foreach (var rule in rules)
        {
            if (!rule.IsActivated)
            {
                continue;
            }

            foreach (var trigger in rule.EventTriggers)
            {
                if (onlyChanged && !routes.NeedsResolving(trigger.SourceRoute, trigger.SourceRouteVersion))
                {
                    continue;
                }

                var source = ResolveSource(trigger);
                if (!ReferenceEquals(source, trigger.Source))
                {
                    Unsubscribe(trigger);
                    Subscribe(trigger, source);
                }
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
        var evaluation = new AutomationEvaluation(routes.Resolver, trigger.Rule.Rule, timeProvider.GetUtcNow(), timeProvider.LocalTimeZone, captured);
        return evaluation.EvaluateCondition(filter) == true;
    }

    private IInterceptorSubject? ResolveSource(CompiledEventTrigger trigger)
    {
        var route = routes.BeginRoute();
        var source = routes.ResolveSubject(trigger.Configuration.SubjectPath, trigger.Rule.Rule, route);
        trigger.SourceRoute = route.ToRoute(trigger.SourceRoute);
        trigger.SourceRouteVersion = route.ResolvedAt;
        return source;
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
