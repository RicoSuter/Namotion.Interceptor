using Namotion.Interceptor;
using Namotion.Interceptor.Expressions;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// An event trigger with its parsed filter and its subscription. <see cref="Generation"/> changes with every subscription,
/// so an event queued from an earlier one is not processed.
/// </summary>
internal sealed class CompiledEventTrigger(CompiledRule rule, int index, SubjectEventTrigger configuration, ParsedExpression? filter)
{
    public CompiledRule Rule { get; } = rule;

    /// <summary>The position of the trigger among all triggers of its rule.</summary>
    public int Index { get; } = index;

    public SubjectEventTrigger Configuration { get; } = configuration;

    public ParsedExpression? Filter { get; } = filter;

    public int Generation { get; set; }

    public IInterceptorSubject? Source { get; set; }

    public IDisposable? Subscription { get; set; }

    /// <summary>Why the trigger receives no events, or null while it is subscribed.</summary>
    public string? SourceError { get; set; }

    public string Description => $"trigger {Index + 1} ({Configuration.EventType} of {Configuration.SubjectPath})";
}
