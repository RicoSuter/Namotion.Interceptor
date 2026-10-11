using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;

namespace HomeBlaze.AI.Agents.Anthropic;

/// <summary>
/// The chat session of an Anthropic agent: the messages since the last compaction plus a summary of everything older.
/// </summary>
/// <remarks>Not thread-safe; the owning agent uses it from one turn at a time.</remarks>
public sealed class AnthropicHistory
{
    private const int ToolResultPreviewLength = 500;
    private const string SummaryPropertyName = "summary";
    private const string MessagesPropertyName = "messages";

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

    /// <summary>Appends a completed exchange.</summary>
    public void Append(ChatMessage userMessage, IEnumerable<ChatMessage> responseMessages)
    {
        _messages.Add(userMessage);
        _messages.AddRange(responseMessages);
    }

    /// <summary>
    /// Summarizes the older half of the history into <see cref="Summary"/> with one extra model call. The split is
    /// placed before a plain user message, so a tool call and its result are never separated. The history is left
    /// unchanged when the call fails or returns no text.
    /// </summary>
    /// <param name="client">The client of the summarizing call.</param>
    /// <param name="options">The options of the summarizing call, for example its model and output token limit.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public async Task CompactAsync(IChatClient client, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var split = FindSplit();
        if (split <= 0)
        {
            return;
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
        if (string.IsNullOrWhiteSpace(summary))
        {
            return;
        }

        Summary = summary;
        _messages.RemoveRange(0, split);
    }

    /// <summary>Serializes the summary and messages for the session file.</summary>
    public string Serialize()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
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

        if (root.TryGetProperty(MessagesPropertyName, out var messages) && messages.ValueKind == JsonValueKind.Array &&
            messages.Deserialize(MessagesTypeInfo) is { } restored)
        {
            history._messages.AddRange(restored);
        }

        return history;
    }

    private int FindSplit()
    {
        var middle = _messages.Count / 2;
        for (var index = middle; index > 0; index--)
        {
            if (IsPlainUserMessage(_messages[index]))
            {
                return index;
            }
        }

        for (var index = middle + 1; index < _messages.Count; index++)
        {
            if (IsPlainUserMessage(_messages[index]))
            {
                return index;
            }
        }

        return 0;
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
                case FunctionResultContent result:
                    transcript.Append(" [result ").Append(Truncate(JsonSerializer.Serialize(result.Result, ValueTypeInfo))).Append(']');
                    break;
            }
        }

        transcript.AppendLine();
    }

    private static string Truncate(string text) =>
        text.Length <= ToolResultPreviewLength ? text : text[..ToolResultPreviewLength] + "...";
}
