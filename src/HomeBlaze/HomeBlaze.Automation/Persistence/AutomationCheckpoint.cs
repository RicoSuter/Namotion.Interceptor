namespace HomeBlaze.Automation.Persistence;

/// <summary>
/// The runtime state of an automation that survives a restart: its state, when it entered it, and the last run of each
/// rule by key. It holds no list, so its size does not grow with the number of runs.
/// </summary>
internal sealed record AutomationCheckpoint
{
    public string? CurrentState { get; init; }

    public DateTimeOffset? StateEnteredAt { get; init; }

    public IReadOnlyDictionary<string, RuleCheckpoint> Rules { get; init; } = new Dictionary<string, RuleCheckpoint>();
}

/// <summary>The run properties of one rule.</summary>
internal sealed record RuleCheckpoint
{
    public DateTimeOffset? LastTriggeredAt { get; init; }

    public long RunCount { get; init; }

    public AutomationRunOutcome? LastRunOutcome { get; init; }

    public string? LastRunMessage { get; init; }
}
