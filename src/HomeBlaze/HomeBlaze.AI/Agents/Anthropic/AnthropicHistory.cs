using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;

namespace HomeBlaze.AI.Agents.Anthropic;

/// <summary>
/// The outcome of <see cref="AnthropicHistory.CompactAsync"/>.
/// </summary>
public enum AnthropicCompactionOutcome
{
    /// <summary>The older messages were replaced by a new summary.</summary>
    Compacted,

    /// <summary>The history holds at most one exchange, so nothing could be summarized and no model call was made.</summary>
    NothingToCompact,

    /// <summary>The model returned no summary or ran out of output tokens; the history is unchanged.</summary>
    SummaryIncomplete
}

/// <summary>
/// The chat session of an Anthropic agent: the messages since the last compaction plus a summary of everything older.
/// </summary>
/// <remarks>
/// Not thread-safe; the owning agent uses it from one turn at a time. Stored messages are normalized so that a
/// restored history sends the same request bytes as the original, which keeps the prompt cache valid.
/// </remarks>
public sealed class AnthropicHistory
{
    private const int ToolResultPreviewLength = 500;
    private const string SummaryPropertyName = "summary";
    private const string MessagesPropertyName = "messages";

    // FunctionResultContent.Exception is not serialized, but the client sends is_error from it.
    private const string IsErrorPropertyName = "isError";

    private const string SummarizerInstructions =
        "Summarize this conversation between an operator and a home automation agent. Keep facts, decisions, open questions and values the agent may need later. Be concise.";

    // The source-generated contracts of Microsoft.Extensions.AI cover messages, their content types and the tool
    // values the Anthropic client itself serializes, so persisting and describing them needs no further metadata.
    private static readonly JsonTypeInfo<IList<ChatMessage>> MessagesTypeInfo =
        (JsonTypeInfo<IList<ChatMessage>>)AIJsonUtilities.DefaultOptions.GetTypeInfo(typeof(IList<ChatMessage>));

    private static readonly JsonTypeInfo<object?> ValueTypeInfo =
        (JsonTypeInfo<object?>)AIJsonUtilities.DefaultOptions.GetTypeInfo(typeof(object));

    private readonly List<ChatMessage> _messages = [];

    /// <summary>Messages since the last compaction, oldest first.</summary>
    public IReadOnlyList<ChatMessage> Messages => _messages;

    /// <summary>A summary of the compacted conversation, or null.</summary>
    public string? Summary { get; private set; }

    /// <summary>Restores a persisted summary.</summary>
    public void RestoreSummary(string? summary) => Summary = summary;

    /// <summary>
    /// The messages of a request: system prompt, summary (as a user message), history, then the new user message.
    /// </summary>
    public IReadOnlyList<ChatMessage> BuildRequestMessages(string systemPrompt, string userMessage)
    {
        var messages = new List<ChatMessage>(_messages.Count + 3) { new(ChatRole.System, systemPrompt) };
        if (Summary is not null)
        {
            messages.Add(new ChatMessage(ChatRole.User, "Summary of the earlier conversation:\n" + Summary));
        }

        messages.AddRange(_messages);
        messages.Add(new ChatMessage(ChatRole.User, userMessage));
        return messages;
    }

    /// <summary>
    /// Appends a completed exchange. Reasoning is not kept: its signatures are bound to the exact request it was
    /// produced for, which a later turn changes. Tool results are stored as the JSON text the client sends.
    /// </summary>
    public void Append(ChatMessage userMessage, IEnumerable<ChatMessage> responseMessages)
    {
        _messages.Add(userMessage);
        foreach (var message in responseMessages)
        {
            if (ToStoredMessage(message) is { } stored)
            {
                _messages.Add(stored);
            }
        }
    }

    /// <summary>
    /// Summarizes the older messages into <see cref="Summary"/> with one extra model call. The split is placed
    /// before a plain user message, so a tool call and its result are never separated, and keeps the longest tail
    /// whose estimated size is at most <paramref name="keepFraction"/> of the history, but at least the last
    /// exchange. The history is left unchanged when the call fails, returns no text or runs out of output tokens.
    /// </summary>
    /// <param name="client">The client of the summarizing call.</param>
    /// <param name="options">The options of the summarizing call, for example its model and output token limit.</param>
    /// <param name="keepFraction">The share of the history, by estimated size, to keep unsummarized.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public async Task<AnthropicCompactionOutcome> CompactAsync(
        IChatClient client, ChatOptions? options = null, double keepFraction = 0.5, CancellationToken cancellationToken = default)
    {
        var split = FindSplit(keepFraction);
        if (split <= 0)
        {
            return AnthropicCompactionOutcome.NothingToCompact;
        }

        var transcript = new StringBuilder();
        if (Summary is not null)
        {
            transcript.Append("Earlier summary:\n").AppendLine(Summary);
        }

        for (var index = 0; index < split; index++)
        {
            AppendDescription(transcript, _messages[index]);
        }

        var response = await client.GetResponseAsync(
        [
            new ChatMessage(ChatRole.System, SummarizerInstructions),
            new ChatMessage(ChatRole.User, transcript.ToString())
        ], options, cancellationToken);

        var summary = response.Text;
        if (string.IsNullOrWhiteSpace(summary) || response.FinishReason == ChatFinishReason.Length)
        {
            return AnthropicCompactionOutcome.SummaryIncomplete;
        }

        Summary = summary;
        _messages.RemoveRange(0, split);
        return AnthropicCompactionOutcome.Compacted;
    }

    /// <summary>Serializes the summary and messages for the session file.</summary>
    public string Serialize()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = AIJsonUtilities.DefaultOptions.Encoder }))
        {
            writer.WriteStartObject();
            writer.WriteString(SummaryPropertyName, Summary);
            writer.WritePropertyName(MessagesPropertyName);
            JsonSerializer.Serialize(writer, _messages, MessagesTypeInfo);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    /// <summary>Restores a history written by <see cref="Serialize"/>.</summary>
    /// <exception cref="JsonException">The content is not a valid history.</exception>
    public static AnthropicHistory Deserialize(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("The history must be a JSON object.");
        }

        var history = new AnthropicHistory();
        if (root.TryGetProperty(SummaryPropertyName, out var summary) && summary.ValueKind == JsonValueKind.String)
        {
            history.Summary = summary.GetString();
        }

        IList<ChatMessage>? restored = null;
        if (root.TryGetProperty(MessagesPropertyName, out var messages) && messages.ValueKind == JsonValueKind.Array)
        {
            try
            {
                restored = messages.Deserialize(MessagesTypeInfo);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or InvalidOperationException)
            {
                // Content constructors reject invalid values, for example a null or empty role.
                throw new JsonException("The history contains an invalid message: " + exception.Message, exception);
            }
        }

        if (restored is not null)
        {
            foreach (var message in restored)
            {
                if (message is not null)
                {
                    RestoreToolResults(message);
                    history._messages.Add(message);
                }
            }
        }

        return history;
    }

    private static ChatMessage? ToStoredMessage(ChatMessage message)
    {
        var contents = new List<AIContent>(message.Contents.Count);
        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextReasoningContent:
                    break;
                case FunctionResultContent result:
                    contents.Add(NormalizeToolResult(result));
                    break;
                default:
                    contents.Add(content);
                    break;
            }
        }

        if (contents.Count == 0)
        {
            return null;
        }

        return new ChatMessage(message.Role, contents)
        {
            AuthorName = message.AuthorName,
            MessageId = message.MessageId,
            CreatedAt = message.CreatedAt,
            AdditionalProperties = message.AdditionalProperties
        };
    }

    private static FunctionResultContent NormalizeToolResult(FunctionResultContent result)
    {
        // The client sends strings as they are, content as content blocks, and anything else as its JSON text.
        // Content results are restored as JSON text, which the client sends differently; no tool returns content today.
        var value = result.Result is null or string or AIContent or IEnumerable<AIContent>
            ? result.Result
            : JsonSerializer.Serialize(result.Result, ValueTypeInfo);

        var normalized = new FunctionResultContent(result.CallId, value)
        {
            Exception = result.Exception,
            AdditionalProperties = result.AdditionalProperties
        };

        if (result.Exception is not null)
        {
            var properties = result.AdditionalProperties is { } existing ? new AdditionalPropertiesDictionary(existing) : [];
            properties[IsErrorPropertyName] = true;
            normalized.AdditionalProperties = properties;
        }

        return normalized;
    }

    private static void RestoreToolResults(ChatMessage message)
    {
        foreach (var content in message.Contents)
        {
            if (content is not FunctionResultContent result)
            {
                continue;
            }

            if (result.Result is JsonElement element)
            {
                result.Result = element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();
            }

            if (result.AdditionalProperties?.TryGetValue(IsErrorPropertyName, out var isError) == true &&
                isError is true or JsonElement { ValueKind: JsonValueKind.True })
            {
                result.Exception = new RestoredToolCallException();
            }
        }
    }

    private int FindSplit(double keepFraction)
    {
        var lengths = new long[_messages.Count];
        long total = 0;
        for (var index = 0; index < _messages.Count; index++)
        {
            lengths[index] = EstimateLength(_messages[index]);
            total += lengths[index];
        }

        var keepLimit = Math.Clamp(keepFraction, 0, 1) * total;
        var lastPlainUserMessage = 0;
        long tail = 0;
        var split = 0;
        for (var index = _messages.Count - 1; index > 0; index--)
        {
            tail += lengths[index];
            if (IsPlainUserMessage(_messages[index]))
            {
                if (lastPlainUserMessage == 0)
                {
                    lastPlainUserMessage = index;
                }

                if (tail <= keepLimit)
                {
                    split = index;
                }
            }
        }

        // At least the last exchange stays, even when it alone is larger than the share to keep.
        return split > 0 ? split : lastPlainUserMessage;
    }

    private static long EstimateLength(ChatMessage message)
    {
        long length = 0;
        foreach (var content in message.Contents)
        {
            length += content switch
            {
                TextContent text => text.Text.Length,
                FunctionCallContent call => call.Name.Length + JsonSerializer.Serialize<object?>(call.Arguments, ValueTypeInfo).Length,
                FunctionResultContent { Result: string text } => text.Length,
                FunctionResultContent result => JsonSerializer.Serialize(result.Result, ValueTypeInfo).Length,
                _ => 0
            };
        }

        return length;
    }

    private static bool IsPlainUserMessage(ChatMessage message) =>
        message.Role == ChatRole.User && message.Contents.All(content => content is TextContent);

    private static void AppendDescription(StringBuilder transcript, ChatMessage message)
    {
        transcript.Append(message.Role.Value).Append(':');
        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextContent text:
                    transcript.Append(' ').Append(text.Text);
                    break;
                case FunctionCallContent call:
                    transcript.Append(" [called ").Append(call.Name).Append(' ')
                        .Append(JsonSerializer.Serialize<object?>(call.Arguments, ValueTypeInfo)).Append(']');
                    break;
                case FunctionResultContent { Result: string text }:
                    transcript.Append(" [result ").Append(Truncate(text)).Append(']');
                    break;
                case FunctionResultContent result:
                    transcript.Append(" [result ").Append(Truncate(JsonSerializer.Serialize(result.Result, ValueTypeInfo))).Append(']');
                    break;
            }
        }

        transcript.AppendLine();
    }

    private static string Truncate(string text) =>
        text.Length <= ToolResultPreviewLength ? text : text[..ToolResultPreviewLength] + "...";

    /// <summary>Marks a restored tool result that failed, so the request flags it as an error again.</summary>
    private sealed class RestoredToolCallException : Exception
    {
        public RestoredToolCallException()
            : base("The tool call failed.")
        {
        }
    }
}
