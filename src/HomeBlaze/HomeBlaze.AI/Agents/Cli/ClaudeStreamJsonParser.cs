using System.Text.Json;

namespace HomeBlaze.AI.Agents.Cli;

/// <summary>An event of the Claude CLI stream-json output that agents use.</summary>
public abstract record ClaudeStreamEvent;

/// <summary>The session started; carries the id to resume it.</summary>
public sealed record ClaudeInitEvent(string SessionId) : ClaudeStreamEvent;

/// <summary>A chunk of answer text.</summary>
public sealed record ClaudeTextDeltaEvent(string Text) : ClaudeStreamEvent;

/// <summary>Subscription utilization.</summary>
public sealed record ClaudeRateLimitEvent(AgentRateLimit RateLimit) : ClaudeStreamEvent;

/// <summary>An assistant message the CLI marked with an error code, for example <c>authentication_failed</c>.</summary>
public sealed record ClaudeAssistantErrorEvent(string Code) : ClaudeStreamEvent;

/// <summary>The final result of the run.</summary>
public sealed record ClaudeResultEvent(string? SessionId, string Subtype, bool IsError, string? Text, AgentUsage? Usage) : ClaudeStreamEvent;

/// <summary>
/// Parses lines of <c>claude -p --output-format stream-json --verbose --include-partial-messages</c>.
/// </summary>
public static class ClaudeStreamJsonParser
{
    // DateTimeOffset.FromUnixTimeSeconds throws outside this range.
    private static readonly long MinimumUnixSeconds = DateTimeOffset.MinValue.ToUnixTimeSeconds();
    private static readonly long MaximumUnixSeconds = DateTimeOffset.MaxValue.ToUnixTimeSeconds();

    /// <summary>
    /// Parses one line; returns null for lines agents do not use and for malformed lines.
    /// </summary>
    public static ClaudeStreamEvent? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return GetString(root, "type") switch
            {
                "system" when GetString(root, "subtype") == "init" && GetString(root, "session_id") is { } sessionId => new ClaudeInitEvent(sessionId),
                "stream_event" => ParseStreamEvent(root),
                "rate_limit_event" => ParseRateLimit(root),
                "assistant" when GetString(root, "error") is { } code => new ClaudeAssistantErrorEvent(code),
                "result" => ParseResult(root),
                _ => null
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ClaudeTextDeltaEvent? ParseStreamEvent(JsonElement root)
    {
        if (root.TryGetProperty("event", out var streamEvent) &&
            streamEvent.ValueKind == JsonValueKind.Object &&
            GetString(streamEvent, "type") == "content_block_delta" &&
            streamEvent.TryGetProperty("delta", out var delta) &&
            delta.ValueKind == JsonValueKind.Object &&
            GetString(delta, "type") == "text_delta" &&
            GetString(delta, "text") is { } text)
        {
            return new ClaudeTextDeltaEvent(text);
        }

        return null;
    }

    private static ClaudeRateLimitEvent? ParseRateLimit(JsonElement root)
    {
        if (!root.TryGetProperty("rate_limit_info", out var info) || info.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        info.TryGetProperty("unifiedWindows", out var windows);
        var (fiveHourUtilization, fiveHourResetsAt) = ParseWindow(windows, "five_hour");
        var (sevenDayUtilization, sevenDayResetsAt) = ParseWindow(windows, "seven_day");
        return new ClaudeRateLimitEvent(new AgentRateLimit(
            GetString(info, "status") ?? "unknown",
            fiveHourUtilization,
            fiveHourResetsAt,
            sevenDayUtilization,
            sevenDayResetsAt));
    }

    private static (double? Utilization, DateTimeOffset? ResetsAt) ParseWindow(JsonElement windows, string name)
    {
        if (windows.ValueKind != JsonValueKind.Object ||
            !windows.TryGetProperty(name, out var window) ||
            window.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        double? utilization = window.TryGetProperty("utilization", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var fraction)
            ? fraction
            : null;
        DateTimeOffset? resetsAt = window.TryGetProperty("resetsAt", out var reset) && reset.ValueKind == JsonValueKind.Number &&
            reset.TryGetInt64(out var seconds) && seconds >= MinimumUnixSeconds && seconds <= MaximumUnixSeconds
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
        return (utilization, resetsAt);
    }

    private static ClaudeResultEvent ParseResult(JsonElement root)
    {
        AgentUsage? usage = null;
        if (root.TryGetProperty("usage", out var usageElement) && usageElement.ValueKind == JsonValueKind.Object)
        {
            usage = new AgentUsage(
                GetInt64(usageElement, "input_tokens"),
                GetInt64(usageElement, "output_tokens"),
                GetInt64(usageElement, "cache_read_input_tokens"),
                GetInt64(usageElement, "cache_creation_input_tokens"));
        }

        return new ClaudeResultEvent(
            GetString(root, "session_id"),
            GetString(root, "subtype") ?? "unknown",
            root.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True,
            GetString(root, "result"),
            usage);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long GetInt64(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : 0;
}
