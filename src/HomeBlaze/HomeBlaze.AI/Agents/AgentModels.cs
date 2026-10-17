namespace HomeBlaze.AI.Agents;

/// <summary>
/// Whether a turn is a background run (no session) or part of the chat session.
/// </summary>
public enum AgentTurnKind
{
    Background,
    Chat
}

/// <summary>
/// How much the model thinks and how many tokens it spends. Null in configuration means the model default.
/// </summary>
public enum AgentEffort
{
    Low,
    Medium,
    High,
    ExtraHigh,
    Max
}

/// <summary>
/// One turn handed to a backend.
/// </summary>
/// <param name="Kind">Background or chat.</param>
/// <param name="SystemPrompt">The complete system prompt (preamble, instructions, memory).</param>
/// <param name="UserMessage">The user message of this turn.</param>
/// <param name="Model">Model id or alias, null for the backend default.</param>
/// <param name="Effort">Effort, null for the model default.</param>
/// <param name="Tools">The tools this turn may call, already scoped and budgeted.</param>
public sealed record AgentTurnRequest(
    AgentTurnKind Kind,
    string SystemPrompt,
    string UserMessage,
    string? Model,
    AgentEffort? Effort,
    AgentToolSet Tools)
{
    /// <summary>
    /// The names of the backend tools this turn may use, such as <c>web_search</c>: the allowed ones the agent type
    /// supports. The backend records their calls with <see cref="AgentToolSet.RecordBackendCall"/>.
    /// </summary>
    public IReadOnlyList<string> BackendTools { get; init; } = [];
}

/// <summary>
/// The outcome of a successful turn.
/// </summary>
/// <param name="Text">
/// The whole answer as streamed: every text segment of the turn in order, separated by a blank line.
/// </param>
/// <param name="Usage">Token usage, if reported.</param>
/// <param name="RateLimit">Subscription utilization reported by the Claude CLI, null for other backends.</param>
public sealed record AgentTurnResult(string Text, AgentUsage? Usage, AgentRateLimit? RateLimit)
{
    /// <summary>
    /// The text after the last tool call, which decides whether a background run is quiet; defaults to <see cref="Text"/>.
    /// </summary>
    public string FinalText { get; init; } = Text;
}

/// <summary>
/// Token usage of a turn.
/// </summary>
public sealed record AgentUsage(long InputTokens, long OutputTokens, long CacheReadInputTokens, long CacheCreationInputTokens);

/// <summary>
/// Subscription utilization reported by the Claude CLI. Utilization is a fraction between 0 and 1.
/// </summary>
public sealed record AgentRateLimit(
    string Status,
    double? FiveHourUtilization,
    DateTimeOffset? FiveHourResetsAt,
    double? SevenDayUtilization,
    DateTimeOffset? SevenDayResetsAt);

/// <summary>
/// A tool call made during a turn, recorded for the timeline and audit. <see cref="Arguments"/> and
/// <see cref="ResultPreview"/> are truncated to a preview length, so long arguments may not be valid JSON.
/// </summary>
public sealed record AgentToolCall(string Name, string Arguments, string ResultPreview, bool IsError);

/// <summary>
/// A live update while a turn runs.
/// </summary>
public abstract record AgentTurnUpdate;

/// <summary>Text streamed by the model.</summary>
public sealed record AgentTextDelta(string Text) : AgentTurnUpdate;

/// <summary>A tool call finished.</summary>
public sealed record AgentToolCallCompleted(AgentToolCall Call) : AgentTurnUpdate;

/// <summary>
/// Why a turn failed, which decides how the scheduler reacts.
/// </summary>
public enum AgentFailureKind
{
    /// <summary>Retry with backoff.</summary>
    Transient,

    /// <summary>A usage or rate limit; pause until <see cref="AgentTurnException.RetryAfter"/> when known.</summary>
    UsageLimit,

    /// <summary>Missing CLI, missing login or rejected credentials; pause until the configuration changes.</summary>
    Configuration
}

/// <summary>
/// A turn failure with its classification.
/// </summary>
public sealed class AgentTurnException : Exception
{
    public AgentTurnException(AgentFailureKind kind, string message, DateTimeOffset? retryAfter = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        RetryAfter = retryAfter;
    }

    /// <summary>The failure classification.</summary>
    public AgentFailureKind Kind { get; }

    /// <summary>When a usage limit resets, if reported.</summary>
    public DateTimeOffset? RetryAfter { get; }
}

/// <summary>
/// An <see cref="IProgress{T}"/> that calls the handler synchronously on the reporting thread, unlike
/// <see cref="Progress{T}"/>, which posts to the captured synchronization context.
/// </summary>
internal sealed class SynchronousProgress<T> : IProgress<T>
{
    private readonly Action<T> _handler;

    public SynchronousProgress(Action<T> handler) => _handler = handler;

    public void Report(T value) => _handler(value);
}
