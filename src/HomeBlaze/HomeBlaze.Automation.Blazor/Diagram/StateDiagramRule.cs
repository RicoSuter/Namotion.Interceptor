namespace HomeBlaze.Automation.Blazor.Diagram;

/// <summary>What the state diagram needs of a rule: its key, name, state scope and actions.</summary>
public sealed record StateDiagramRule(string Key, string Name, string? InState, IReadOnlyList<AutomationAction> Actions)
{
    public static StateDiagramRule From(string key, AutomationRule rule) => new(key, rule.Name ?? key, rule.InState, rule.Actions);
}
