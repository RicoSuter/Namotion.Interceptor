using Namotion.Interceptor;
using Namotion.Interceptor.Expressions;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// An event trigger with its parsed filter and the subscription it uses, which it handles only the events of that it
/// received after it joined.
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

    public EventSubscription? Subscription { get; set; }

    /// <summary>
    /// The sequence number of the last event received before the trigger joined <see cref="Subscription"/>, or before the
    /// configuration that subscribed it was requested; the trigger handles only later events.
    /// </summary>
    public long JoinedAfter { get; set; }

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
    /// queued and received from now on are handled by that one.
    /// </summary>
    public void MoveSubscriptionTo(CompiledEventTrigger target)
    {
        if (Subscription is not { } subscription)
        {
            return;
        }

        target.Subscription = subscription;
        target.JoinedAfter = JoinedAfter;
        target.Source = Source;
        target.SourceError = SourceError;
        Subscription = null;
    }

    public string Description => $"trigger {Index + 1} ({Configuration.EventType} of {Configuration.SubjectPath})";
}
