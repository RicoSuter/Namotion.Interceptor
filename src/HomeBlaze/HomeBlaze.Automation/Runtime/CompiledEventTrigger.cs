using Namotion.Interceptor;
using Namotion.Interceptor.Expressions;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// An event trigger with its parsed filter and its subscription. An event queued by an earlier subscription is not
/// processed.
/// </summary>
internal sealed class CompiledEventTrigger(CompiledRule rule, int index, SubjectEventTrigger configuration, ParsedExpression? filter)
{
    public CompiledRule Rule { get; } = rule;

    /// <summary>The position of the trigger among all triggers of its rule.</summary>
    public int Index { get; } = index;

    public SubjectEventTrigger Configuration { get; } = configuration;

    public ParsedExpression? Filter { get; } = filter;

    public IInterceptorSubject? Source { get; set; }

    /// <summary>The graph route of the last resolution of the path of <see cref="Source"/>.</summary>
    public GraphRouteSlot SourceRoute { get; } = new();

    public EventTriggerSubscription? Subscription { get; set; }

    /// <summary>Why the trigger receives no events, or null while it is subscribed.</summary>
    public string? SourceError
    {
        get;
        set
        {
            if (!string.Equals(field, value, StringComparison.Ordinal))
            {
                field = value;
                Version++;
            }
        }
    }

    /// <summary>Increases with every change of <see cref="SourceError"/>, so that a status is formatted again only after one.</summary>
    public int Version { get; private set; }

    /// <summary>
    /// Hands the subscription to the trigger that replaces this one with an equal configuration, so that the events
    /// queued and received from now on run on that one.
    /// </summary>
    public void MoveSubscriptionTo(CompiledEventTrigger target)
    {
        if (Subscription is not { } subscription)
        {
            return;
        }

        subscription.Trigger = target;
        target.Subscription = subscription;
        target.Source = Source;
        target.SourceError = SourceError;
        Subscription = null;
    }

    public string Description => $"trigger {Index + 1} ({Configuration.EventType} of {Configuration.SubjectPath})";
}
