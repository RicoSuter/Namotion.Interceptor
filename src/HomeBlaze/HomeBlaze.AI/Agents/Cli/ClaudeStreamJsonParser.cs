using System.Text.Json;

namespace HomeBlaze.AI.Agents.Cli;

/// <summary>An event of the Claude CLI stream-json output that agents use.</summary>
internal abstract record ClaudeStreamEvent;

/// <summary>The session started; carries the id to resume it.</summary>
internal sealed record ClaudeInitEvent(string SessionId) : ClaudeStreamEvent;

/// <summary>A chunk of answer text.</summary>
internal sealed record ClaudeTextDeltaEvent(string Text) : ClaudeStreamEvent;

/// <summary>A model message or one of its content blocks started, so the next text starts a new segment.</summary>
internal sealed record ClaudeBlockStartEvent : ClaudeStreamEvent
{
    /// <summary>The shared instance; the event carries no data.</summary>
    public static readonly ClaudeBlockStartEvent Instance = new();
}

/// <summary>Subscription utilization.</summary>
internal sealed record ClaudeRateLimitEvent(AgentRateLimit RateLimit) : ClaudeStreamEvent;

/// <summary>An assistant message the CLI marked with an error code, for example <c>authentication_failed</c>.</summary>
internal sealed record ClaudeAssistantErrorEvent(string Code) : ClaudeStreamEvent;

/// <summary>A tool call the model made, as a complete assistant message reports it.</summary>
/// <param name="Id">The tool use id, which the result refers to.</param>
/// <param name="Name">The tool name, for example <c>WebSearch</c> or <c>mcp__homeblaze__browse</c>.</param>
/// <param name="Arguments">The input as JSON.</param>
internal sealed record ClaudeToolUse(string Id, string Name, string Arguments);

/// <summary>The tool calls of an assistant message.</summary>
internal sealed record ClaudeToolUseEvent(IReadOnlyList<ClaudeToolUse> ToolUses) : ClaudeStreamEvent;

/// <summary>The result of a tool call.</summary>
/// <param name="ToolUseId">The id of the tool use.</param>
/// <param name="Content">The text of the result.</param>
/// <param name="IsError">Whether the call failed.</param>
internal sealed record ClaudeToolResult(string ToolUseId, string Content, bool IsError);

/// <summary>The tool results of a user message.</summary>
internal sealed record ClaudeToolResultEvent(IReadOnlyList<ClaudeToolResult> Results) : ClaudeStreamEvent;

/// <summary>The final result of the run.</summary>
internal sealed record ClaudeResultEvent(string? SessionId, string Subtype, bool IsError, string? Text, AgentUsage? Usage) : ClaudeStreamEvent;

/// <summary>
/// Parses lines of <c>claude -p --output-format stream-json --verbose --include-partial-messages</c>.
/// </summary>
internal static class ClaudeStreamJsonParser
{
    // DateTimeOffset.FromUnixTimeSeconds throws outside this range.
    private const long MinimumUnixSeconds = -62_135_596_800;
    private const long MaximumUnixSeconds = 253_402_300_799;

    /// <summary>
    /// Parses one line; returns null for lines agents do not use and for malformed lines, which are dropped
    /// (including lines with string values that cannot be decoded, such as unpaired surrogate escapes).
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

            if (!root.TryGetProperty("type"u8, out var type) || type.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return ParseByType(root, type);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static ClaudeStreamEvent? ParseByType(JsonElement root, JsonElement type)
    {
        if (type.ValueEquals("stream_event"u8))
        {
            return ParseStreamEvent(root);
        }

        if (type.ValueEquals("system"u8))
        {
            return HasString(root, "subtype"u8, "init"u8) && GetString(root, "session_id"u8) is { } sessionId
                ? new ClaudeInitEvent(sessionId)
                : null;
        }

        if (type.ValueEquals("rate_limit_event"u8))
        {
            return ParseRateLimit(root);
        }

        if (type.ValueEquals("assistant"u8))
        {
            return GetString(root, "error"u8) is { } code ? new ClaudeAssistantErrorEvent(code) : ParseToolUses(root);
        }

        if (type.ValueEquals("user"u8))
        {
            return ParseToolResults(root);
        }

        return type.ValueEquals("result"u8) ? ParseResult(root) : null;
    }

    private static ClaudeStreamEvent? ParseStreamEvent(JsonElement root)
    {
        if (!TryGetObject(root, "event"u8, out var streamEvent))
        {
            return null;
        }

        if (HasString(streamEvent, "type"u8, "content_block_start"u8) || HasString(streamEvent, "type"u8, "message_start"u8))
        {
            return ClaudeBlockStartEvent.Instance;
        }

        if (!HasString(streamEvent, "type"u8, "content_block_delta"u8) ||
            !TryGetObject(streamEvent, "delta"u8, out var delta))
        {
            return null;
        }

        return HasString(delta, "type"u8, "text_delta"u8) && GetString(delta, "text"u8) is { } text
            ? new ClaudeTextDeltaEvent(text)
            : null;
    }

    private static ClaudeToolUseEvent? ParseToolUses(JsonElement root)
    {
        List<ClaudeToolUse>? toolUses = null;
        foreach (var block in GetContentBlocks(root))
        {
            if (HasString(block, "type"u8, "tool_use"u8) &&
                GetString(block, "id"u8) is { } id &&
                GetString(block, "name"u8) is { } name)
            {
                var arguments = block.TryGetProperty("input"u8, out var input) ? input.GetRawText() : "{}";
                (toolUses ??= []).Add(new ClaudeToolUse(id, name, arguments));
            }
        }

        return toolUses is null ? null : new ClaudeToolUseEvent(toolUses);
    }

    private static ClaudeToolResultEvent? ParseToolResults(JsonElement root)
    {
        List<ClaudeToolResult>? results = null;
        foreach (var block in GetContentBlocks(root))
        {
            if (HasString(block, "type"u8, "tool_result"u8) && GetString(block, "tool_use_id"u8) is { } toolUseId)
            {
                var isError = block.TryGetProperty("is_error"u8, out var error) && error.ValueKind == JsonValueKind.True;
                (results ??= []).Add(new ClaudeToolResult(toolUseId, GetResultText(block), isError));
            }
        }

        return results is null ? null : new ClaudeToolResultEvent(results);
    }

    private static JsonElement.ArrayEnumerator GetContentBlocks(JsonElement root) =>
        TryGetObject(root, "message"u8, out var message) &&
        message.TryGetProperty("content"u8, out var content) &&
        content.ValueKind == JsonValueKind.Array
            ? content.EnumerateArray()
            : default;

    // A result is a string or a list of content blocks, of which the text blocks are joined.
    private static string GetResultText(JsonElement block)
    {
        if (!block.TryGetProperty("content"u8, out var content))
        {
            return string.Empty;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return content.GetRawText();
        }

        var texts = new List<string>();
        foreach (var item in content.EnumerateArray())
        {
            if (HasString(item, "type"u8, "text"u8) && GetString(item, "text"u8) is { } text)
            {
                texts.Add(text);
            }
        }

        return string.Join('\n', texts);
    }

    private static ClaudeRateLimitEvent? ParseRateLimit(JsonElement root)
    {
        if (!root.TryGetProperty("rate_limit_info"u8, out var info) || info.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        info.TryGetProperty("unifiedWindows"u8, out var windows);
        var (fiveHourUtilization, fiveHourResetsAt) = ParseWindow(windows, "five_hour"u8);
        var (sevenDayUtilization, sevenDayResetsAt) = ParseWindow(windows, "seven_day"u8);
        return new ClaudeRateLimitEvent(new AgentRateLimit(
            GetString(info, "status"u8) ?? "unknown",
            fiveHourUtilization,
            fiveHourResetsAt,
            sevenDayUtilization,
            sevenDayResetsAt));
    }

    private static (double? Utilization, DateTimeOffset? ResetsAt) ParseWindow(JsonElement windows, ReadOnlySpan<byte> name)
    {
        if (windows.ValueKind != JsonValueKind.Object ||
            !windows.TryGetProperty(name, out var window) ||
            window.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        double? utilization = TryGetNumber(window, "utilization"u8, out var value) &&
            value.TryGetDouble(out var fraction) && double.IsFinite(fraction)
            ? Math.Clamp(fraction, 0, 1)
            : null;
        DateTimeOffset? resetsAt = TryGetNumber(window, "resetsAt"u8, out var reset) &&
            reset.TryGetInt64(out var seconds) && IsInUnixSecondsRange(seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
        return (utilization, resetsAt);
    }

    private static bool IsInUnixSecondsRange(long seconds) => seconds >= MinimumUnixSeconds && seconds <= MaximumUnixSeconds;

    private static ClaudeResultEvent ParseResult(JsonElement root)
    {
        AgentUsage? usage = null;
        if (root.TryGetProperty("usage"u8, out var usageElement) && usageElement.ValueKind == JsonValueKind.Object)
        {
            usage = new AgentUsage(
                GetInt64(usageElement, "input_tokens"u8),
                GetInt64(usageElement, "output_tokens"u8),
                GetInt64(usageElement, "cache_read_input_tokens"u8),
                GetInt64(usageElement, "cache_creation_input_tokens"u8));
        }

        return new ClaudeResultEvent(
            GetString(root, "session_id"u8),
            GetString(root, "subtype"u8) ?? "unknown",
            root.TryGetProperty("is_error"u8, out var isError) && isError.ValueKind == JsonValueKind.True,
            GetString(root, "result"u8),
            usage);
    }

    private static bool TryGetObject(JsonElement element, ReadOnlySpan<byte> name, out JsonElement value) =>
        element.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object;

    private static bool TryGetNumber(JsonElement element, ReadOnlySpan<byte> name, out JsonElement value) =>
        element.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Number;

    private static bool HasString(JsonElement element, ReadOnlySpan<byte> name, ReadOnlySpan<byte> expected) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.ValueEquals(expected);

    private static string? GetString(JsonElement element, ReadOnlySpan<byte> name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long GetInt64(JsonElement element, ReadOnlySpan<byte> name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : 0;
}
