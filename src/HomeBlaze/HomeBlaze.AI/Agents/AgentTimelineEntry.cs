namespace HomeBlaze.AI.Agents;

/// <summary>
/// The kind of a timeline entry.
/// </summary>
public enum AgentTimelineEntryKind
{
    User,
    Assistant,
    BackgroundRun,
    Error,
    Divider
}

/// <summary>
/// One entry of an agent's timeline, which combines chat and background runs.
/// </summary>
public sealed record AgentTimelineEntry
{
    public required AgentTimelineEntryKind Kind { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public string Text { get; init; } = string.Empty;

    /// <summary>Whether a background run reported nothing noteworthy.</summary>
    public bool IsQuiet { get; init; }

    /// <summary>
    /// Why a background run started, for example <c>poll</c>, <c>change</c> or <c>manual</c>; also set on the error
    /// entry of a background run that failed or was cancelled, and null on chat entries.
    /// </summary>
    public string? Trigger { get; init; }

    public IReadOnlyList<AgentToolCall> ToolCalls { get; init; } = [];

    public TimeSpan? Duration { get; init; }

    public AgentUsage? Usage { get; init; }
}
