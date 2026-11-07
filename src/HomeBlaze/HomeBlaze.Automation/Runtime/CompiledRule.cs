using HomeBlaze.Automation.Expressions;
using Namotion.Interceptor;
using Namotion.Interceptor.Expressions;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// A validated rule: its configuration as it was when compiled, its parsed expressions and the runtime state of its
/// triggers. Used by the worker only.
/// </summary>
internal sealed class CompiledRule(string key, AutomationRule rule, ParsedExpression? guard)
{
    public string Key { get; } = key;

    /// <summary>The rule subject; a reload that replaces the instance under the same key rebinds it.</summary>
    public AutomationRule Rule { get; set; } = rule;

    public string Name { get; } = rule.Name ?? key;

    public AutomationRuleMode Mode { get; } = rule.Mode;

    public string? InState { get; } = rule.InState;

    public ParsedExpression? Guard { get; } = guard;

    /// <summary>The paths of the guard that are no variable.</summary>
    public IReadOnlyList<string> GuardPropertyPaths { get; } = guard is null ? [] : [.. AutomationExpressionCheck.GetPropertyPaths(guard)];

    /// <summary>When the guard can change with the clock alone, or null for a guard that does not read the clock.</summary>
    public ExpressionClock? GuardClock { get; } = guard is null ? null : ExpressionClock.Create(guard);

    /// <summary>The properties that <see cref="GuardPropertyPaths"/> resolved to while a trigger of the rule is pending.</summary>
    public PropertyReference[] GuardDependencies { get; set; } = [];

    /// <summary>When the guard of a rule with a pending trigger is evaluated again for the clock, or null for no such instant.</summary>
    public DateTimeOffset? GuardRecheckAt { get; set; }

    /// <summary>Whether the guard of a rule with a pending trigger is evaluated again every second.</summary>
    public bool RechecksGuardEverySecond { get; set; }

    public IReadOnlyList<AutomationAction> Actions { get; } = [.. rule.Actions];

    /// <summary>Whether an event can be captured for a run of the rule, so its arguments may read <c>event</c> and <c>subject</c>.</summary>
    public bool AllowsEvent { get; } = rule.Triggers.Any(trigger => trigger is SubjectEventTrigger);

    public List<CompiledPropertyTrigger> PropertyTriggers { get; } = [];

    public List<CompiledEventTrigger> EventTriggers { get; } = [];

    /// <summary>Whether the rule is active: its triggers are subscribed or about to evaluate their activation.</summary>
    public bool IsActivated { get; set; }

    /// <summary>Why the last request of the rule did not run, until a request runs or the rule is deactivated; null otherwise.</summary>
    public string? SkipReason { get; set; }

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
