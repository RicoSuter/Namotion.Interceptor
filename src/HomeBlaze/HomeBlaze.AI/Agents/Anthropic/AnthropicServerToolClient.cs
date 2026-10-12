using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Anthropic.Core;
using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;

namespace HomeBlaze.AI.Agents.Anthropic;

/// <summary>
/// Sits between the function-invoking client and the Anthropic client of a turn that uses the API's web tools, which
/// the API runs itself (server tools). It records their calls in the turn's tool set, keeps their blocks in the
/// messages the function-invoking client sends back within the turn, and continues a response the API paused.
/// </summary>
/// <remarks>
/// The Anthropic client turns server tool blocks into contents that it drops when the messages are sent again, and its
/// streamed server tool call keeps only a search query, not the URL of a fetch. Without the blocks, a request after a
/// HomeBlaze tool call would lose the search results the model already used. Not disposed by its owner: disposing it
/// would dispose the reused Anthropic client.
/// </remarks>
internal sealed class AnthropicServerToolClient : DelegatingChatClient
{
    // The API pauses a long server tool loop with this stop reason; sending the response back continues it.
    private const string PauseTurnReason = "pause_turn";
    private const int MaximumContinuations = 5;

    private readonly AgentToolSet _tools;

    public AnthropicServerToolClient(IChatClient innerClient, AgentToolSet tools)
        : base(innerClient)
    {
        _tools = tools;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var tracker = new ServerToolTracker(_tools);
        var requestMessages = messages;
        var continuations = 0;
        while (true)
        {
            var updates = new List<ChatResponseUpdate>();
            await foreach (var update in base.GetStreamingResponseAsync(requestMessages, options, cancellationToken))
            {
                tracker.Observe(update);
                updates.Add(update);
                yield return update;
            }

            // The caller sends these contents back only after the stream ended, so they are complete in time.
            tracker.CompletePendingCalls();
            var finishReason = updates.LastOrDefault(update => update.FinishReason is not null)?.FinishReason;
            if (finishReason?.Value != PauseTurnReason || continuations++ >= MaximumContinuations)
            {
                yield break;
            }

            requestMessages = [.. requestMessages, .. updates.ToChatResponse().Messages];
        }
    }

    /// <summary>
    /// Pairs server tool calls with their results, records them and replaces the raw response blocks of both with
    /// the request blocks the Anthropic client sends as they are.
    /// </summary>
    private sealed class ServerToolTracker(AgentToolSet tools)
    {
        private readonly Dictionary<string, PendingCall> _pendingCalls = new(StringComparer.Ordinal);

        public void Observe(ChatResponseUpdate update)
        {
            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case WebSearchToolCallContent call when call.RawRepresentation is JsonModel block:
                        _pendingCalls[call.CallId] = new PendingCall(call, block.RawData);
                        break;
                    case WebSearchToolResultContent result when result.RawRepresentation is JsonModel block:
                        result.RawRepresentation = new ContentBlockParam(ToElement(block.RawData));
                        if (_pendingCalls.Remove(result.CallId, out var pendingCall))
                        {
                            Complete(pendingCall, block.RawData);
                        }

                        break;
                }
            }
        }

        public void CompletePendingCalls()
        {
            foreach (var pendingCall in _pendingCalls.Values)
            {
                Complete(pendingCall, result: null);
            }

            _pendingCalls.Clear();
        }

        private void Complete(PendingCall pendingCall, IReadOnlyDictionary<string, JsonElement>? result)
        {
            var (call, block) = pendingCall;
            var name = block.TryGetValue("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()!
                : "web_search";
            var input = GetInput(call, block, result);
            call.RawRepresentation = new ContentBlockParam(ToElement(block, replacedInput: input));

            var (preview, isError) = result is null ? ("No result.", true) : DescribeResult(result);
            tools.RecordBackendCall(name, input.GetRawText(), preview, isError);
        }

        // The streamed call block holds an empty input; the query survives in the content, the URL of a fetch only in its result.
        private static JsonElement GetInput(WebSearchToolCallContent call, IReadOnlyDictionary<string, JsonElement> block, IReadOnlyDictionary<string, JsonElement>? result)
        {
            if (block.TryGetValue("input", out var input) && input.ValueKind == JsonValueKind.Object && input.EnumerateObject().Any())
            {
                return input;
            }

            if (call.Queries is [var query, ..])
            {
                return WriteObject(writer => writer.WriteString("query", query));
            }

            var url = result is not null && result.TryGetValue("content", out var content) ? GetString(content, "url") : null;
            return url is null ? WriteObject(_ => { }) : WriteObject(writer => writer.WriteString("url", url));
        }

        // A search result holds a list of pages, a fetch result one document, and a failure an error code.
        private static (string Preview, bool IsError) DescribeResult(IReadOnlyDictionary<string, JsonElement> result)
        {
            if (!result.TryGetValue("content", out var content))
            {
                return (string.Empty, false);
            }

            if (content.ValueKind == JsonValueKind.Object && content.TryGetProperty("error_code", out var errorCode))
            {
                return ("Error: " + errorCode, true);
            }

            var preview = new StringBuilder();
            var pages = content.ValueKind == JsonValueKind.Array ? content.EnumerateArray().ToList() : [content];
            foreach (var page in pages.Where(page => page.ValueKind == JsonValueKind.Object))
            {
                var title = GetString(page, "title") ?? (page.TryGetProperty("content", out var document) ? GetString(document, "title") : null);
                if (preview.Length > 0)
                {
                    preview.Append('\n');
                }

                preview.Append(title is null ? string.Empty : title + " ").Append(GetString(page, "url"));
            }

            return (preview.ToString(), false);
        }

        private static string? GetString(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static JsonElement ToElement(IReadOnlyDictionary<string, JsonElement> properties, JsonElement? replacedInput = null) =>
            WriteObject(writer =>
            {
                foreach (var (name, value) in properties)
                {
                    writer.WritePropertyName(name);
                    (name == "input" && replacedInput is { } input ? input : value).WriteTo(writer);
                }

                if (replacedInput is { } addedInput && !properties.ContainsKey("input"))
                {
                    writer.WritePropertyName("input");
                    addedInput.WriteTo(writer);
                }
            });

        private static JsonElement WriteObject(Action<Utf8JsonWriter> writeProperties)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writeProperties(writer);
                writer.WriteEndObject();
            }

            using var document = JsonDocument.Parse(buffer.WrittenMemory);
            return document.RootElement.Clone();
        }

        private sealed record PendingCall(WebSearchToolCallContent Call, IReadOnlyDictionary<string, JsonElement> Block);
    }
}
