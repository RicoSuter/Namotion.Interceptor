using HomeBlaze.Automation.Expressions;
using Namotion.Interceptor;
using Namotion.Interceptor.Expressions;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// A validated rule: its configuration as it was when compiled, its parsed expressions and the runtime state of its
/// triggers. Used by the worker only.
/// </summary>
internal sealed class CompiledRule(string key, AutomationRule rule, ParsedExpression? onlyIf)
{
    private int _version;

    public string Key { get; } = key;

    /// <summary>The rule subject; a reload that replaces the instance under the same key rebinds it.</summary>
    public AutomationRule Rule { get; set; } = rule;

    public string Name { get; } = rule.Name ?? key;

    public AutomationRuleMode Mode { get; } = rule.Mode;

    public string? InState { get; } = rule.InState;

    public ParsedExpression? OnlyIf { get; } = onlyIf;

    /// <summary>The paths of Only if that are no variable.</summary>
    public IReadOnlyList<string> OnlyIfPropertyPaths { get; } = onlyIf is null ? [] : [.. AutomationExpressionCheck.GetPropertyPaths(onlyIf)];

    /// <summary>When Only if can change with the clock alone, or null for an Only if that does not read the clock.</summary>
    public ExpressionClock? OnlyIfClock { get; } = onlyIf is null ? null : ExpressionClock.Create(onlyIf);

    /// <summary>The properties that <see cref="OnlyIfPropertyPaths"/> resolved to while a trigger of the rule is pending.</summary>
    public PropertyReference[] OnlyIfDependencies { get; set; } = [];

    /// <summary>The graph route of <see cref="OnlyIfDependencies"/>.</summary>
    public GraphRouteSlot OnlyIfRoute { get; } = new();

    /// <summary>When Only if of a rule with a pending trigger is evaluated again for the clock, or null for no such instant.</summary>
    public DateTimeOffset? OnlyIfRecheckAt { get; set; }

    /// <summary>Whether Only if of a rule with a pending trigger is evaluated again every second.</summary>
    public bool RechecksOnlyIfEverySecond { get; set; }

    public IReadOnlyList<AutomationAction> Actions { get; } = [.. rule.Actions];

    /// <summary>Whether an event can be captured for a run of the rule, so its arguments may read <c>event</c> and <c>subject</c>.</summary>
    public bool AllowsEvent { get; } = rule.Triggers.Any(trigger => trigger is SubjectEventTrigger);

    public List<CompiledPropertyTrigger> PropertyTriggers { get; } = [];

    public List<CompiledEventTrigger> EventTriggers { get; } = [];

    /// <summary>Whether the rule is active: its triggers are subscribed or about to evaluate their activation.</summary>
    public bool IsActivated
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                _version++;
            }
        }
    }

    /// <summary>Whether the event triggers of the rule are subscribed, which they are in an enabled automation while its mode is not Off, whatever the state.</summary>
    public bool ListensForEvents { get; set; }

    /// <summary>Why the last request of the rule did not run, until a request runs or the rule is deactivated; null otherwise.</summary>
    public string? SkipReason
    {
        get;
        set
        {
            if (!string.Equals(field, value, StringComparison.Ordinal))
            {
                field = value;
                _version++;
            }
        }
    }

    /// <summary>
    /// Increases with every change of what the status of the rule shows from the rule itself: its activation, the reason
    /// it did not run and the state of its triggers. Allocates nothing.
    /// </summary>
    public long StatusVersion
    {
        get
        {
            long version = _version;
            foreach (var trigger in PropertyTriggers)
            {
                version += trigger.State.Version;
            }

            foreach (var trigger in EventTriggers)
            {
                version += trigger.Version;
            }

            return version;
        }
    }

    /// <summary>The <see cref="StatusVersion"/> that the status of the rule subject shows, or -1 before one.</summary>
    public long PublishedStatusVersion { get; set; } = -1;

    /// <summary>
    /// Whether the rule observes its triggers in an automation with this enabled flag and current state: only in the modes
    /// <see cref="AutomationRuleMode.DryRun"/> and <see cref="AutomationRuleMode.On"/>.
    /// </summary>
    public bool IsActiveIn(bool isEnabled, string? currentState) =>
        isEnabled && Mode is AutomationRuleMode.DryRun or AutomationRuleMode.On && (InState is null || string.Equals(InState, currentState, StringComparison.Ordinal));

    public IReadOnlyList<TriggerStatus> GetTriggerStatuses()
    {
        var statuses = new List<TriggerStatus>(PropertyTriggers.Count + EventTriggers.Count);
        foreach (var trigger in PropertyTriggers)
        {
            statuses.Add(TriggerStatus.FromProperty(trigger.State, trigger.Configuration.For));
        }

        foreach (var trigger in EventTriggers)
        {
            statuses.Add(TriggerStatus.FromEvent(trigger.SourceError));
        }

        return statuses;
    }
}
