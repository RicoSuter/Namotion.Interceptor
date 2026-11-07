using HomeBlaze.Automation.Blazor.Editing;

namespace HomeBlaze.Automation.Blazor.Diagram;

/// <summary>
/// What the state diagram needs of a rule: its key, name and state scope, the state its only state change enters, and the
/// number of its actions. Two rules with the same values draw the same, so that a layout can be reused.
/// </summary>
/// <param name="Key">The key of the rule.</param>
/// <param name="Name">The name of the rule.</param>
/// <param name="InState">The state the rule is active in, or null for any state.</param>
/// <param name="TargetState">The state of its state change when it has exactly one, otherwise null.</param>
/// <param name="ActionCount">The number of its actions.</param>
public sealed record StateDiagramRule(string Key, string Name, string? InState, string? TargetState, int ActionCount)
{
    public static StateDiagramRule Create(string key, string name, string? inState, IEnumerable<AutomationAction?>? actions)
    {
        var list = (actions ?? []).OfType<AutomationAction>().ToList();
        var targets = list.OfType<ChangeStateAction>().Select(action => action.TargetState).ToList();
        return new StateDiagramRule(key, name, inState, targets.Count == 1 ? targets[0] : null, list.Count);
    }

    public static StateDiagramRule From(string key, AutomationRule rule) => Create(key, rule.Name ?? key, rule.InState, rule.Actions);

    public static StateDiagramRule From(RuleDraft rule) => Create(rule.Key, rule.DisplayName, rule.InState, rule.Actions.Select(action => action.Value));
}
