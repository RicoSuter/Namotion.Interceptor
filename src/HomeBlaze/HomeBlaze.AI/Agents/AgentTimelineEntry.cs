using System.Text.Json.Serialization;

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

    /// <summary>The whole answer of a turn, or the text of a message, error or divider.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>
    /// The text after the last tool call of a background run, which states its result; null when it equals
    /// <see cref="Text"/>, on other entries and in entries written before it existed.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FinalText { get; init; }

    /// <summary>The text that states the result: <see cref="FinalText"/> when set, otherwise <see cref="Text"/>.</summary>
    [JsonIgnore]
    public string ResultText => FinalText ?? Text;

    /// <summary>Whether a background run reported nothing noteworthy.</summary>
    public bool IsQuiet { get; init; }

    /// <summary>
    /// Why a background run started, one of <see cref="AgentTriggers"/>; also set on the error entry of a background
    /// run that failed or was cancelled, and null on chat entries.
    /// </summary>
    public string? Trigger { get; init; }

    public IReadOnlyList<AgentToolCall> ToolCalls { get; init; } = [];

    public TimeSpan? Duration { get; init; }

    public AgentUsage? Usage { get; init; }
}
