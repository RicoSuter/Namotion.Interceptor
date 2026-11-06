namespace HomeBlaze.Automation;

/// <summary>
/// What <see cref="AutomationRule.TestNow"/> found with the current values: the result of each trigger and of the guard,
/// with the paths that led to no value, and the actions with their rendered arguments.
/// </summary>
public sealed record AutomationRuleTestResult
{
    /// <summary>Why the rule cannot be evaluated, such as an invalid expression; the other members are empty then.</summary>
    public string? Error { get; init; }

    /// <summary>Whether the rule observes its triggers now: its mode is not Off and its automation is enabled and in its state.</summary>
    public bool IsActive { get; init; }

    public IReadOnlyList<AutomationTriggerTestResult> Triggers { get; init; } = [];

    public bool HasGuard { get; init; }

    /// <summary>True, false or null for unknown; true without a guard.</summary>
    public bool? GuardResult { get; init; }

    public IReadOnlyList<string> GuardMissingPaths { get; init; } = [];

    /// <summary>The actions as a run would execute them now, such as <c>/Living/Blinds/MoveTo(position: 40)</c>.</summary>
    public IReadOnlyList<string> Actions { get; init; } = [];
}

/// <summary>The result of one trigger in an <see cref="AutomationRuleTestResult"/>.</summary>
public sealed record AutomationTriggerTestResult
{
    /// <summary>The position of the trigger in its rule, from 1.</summary>
    public required int Number { get; init; }

    /// <summary>The expression of a property trigger: true, false or null for unknown. Null for an event trigger.</summary>
    public bool? Result { get; init; }

    public IReadOnlyList<string> MissingPaths { get; init; } = [];

    /// <summary>The hold a property trigger needs, or what an event trigger waits for.</summary>
    public string? Note { get; init; }
}
