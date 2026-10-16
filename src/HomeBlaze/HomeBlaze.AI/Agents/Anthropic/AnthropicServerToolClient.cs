using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
/// HomeBlaze tool call, or the continuation of a paused response, would lose the search results the model already
/// used. This covers every server tool block, including those of the code execution the web tools filter results with.
/// </remarks>
internal sealed class AnthropicServerToolClient : DelegatingChatClient
{
    // The API pauses a long server tool loop with this stop reason; sending the response back continues it.
    private const string PauseTurnReason = "pause_turn";
    private const int MaximumContinuations = 5;

    private readonly AgentToolSet _tools;
    private readonly IReadOnlyList<string> _backendTools;
    private readonly int _maxBackendToolUses;

    /// <param name="innerClient">The Anthropic client.</param>
    /// <param name="tools">The tool set of the turn, which records the server tool calls against its budget.</param>
    /// <param name="backendTools">The backend tools of the turn.</param>
    /// <param name="maxBackendToolUses">The use limit of each server tool per request.</param>
    public AnthropicServerToolClient(IChatClient innerClient, AgentToolSet tools, IReadOnlyList<string> backendTools, int maxBackendToolUses)
        : base(innerClient)
    {
        _tools = tools;
        _backendTools = backendTools;
        _maxBackendToolUses = maxBackendToolUses;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var tracker = new ServerToolTracker(_tools);
        var requestMessages = messages;
        var continuations = 0;

        // The tools lead the cached prompt prefix, so the use limit stays the same across the requests of a turn and
        // across turns instead of following the remaining budget.
        var requestOptions = options is null
            ? null
            : AnthropicChatOptions.WithBackendToolUses(options, _backendTools, _maxBackendToolUses);
        // Also when a request throws or the caller stops early, so that a web call already made shows in the turn.
        try
        {
            while (true)
            {
                var updates = new List<ChatResponseUpdate>();
                await foreach (var update in base.GetStreamingResponseAsync(requestMessages, requestOptions, cancellationToken))
                {
                    tracker.Observe(update);
                    updates.Add(update);
                    yield return update;
                }

                // The caller sends these contents back only after the stream ended, so they are complete in time.
                var finishReason = updates.LastOrDefault(update => update.FinishReason is not null)?.FinishReason;
                if (finishReason?.Value != PauseTurnReason)
                {
                    yield break;
                }

                if (continuations++ >= MaximumContinuations)
                {
                    throw new AgentTurnException(
                        AgentFailureKind.Transient,
                        $"The model paused the turn too often: it was still not done after {MaximumContinuations} continuations.");
                }

                // A call still waiting for its result gets it in the continuation, so it is recorded only then.
                tracker.PreparePendingCallsForResend();
                requestMessages = [.. requestMessages, .. updates.ToChatResponse().Messages];
            }
        }
        finally
        {
            tracker.CompletePendingCalls();
        }
    }

    /// <summary>
    /// Pairs web tool calls with their results and records them, and replaces the raw response blocks of every server
    /// tool call and result with the request blocks the Anthropic client sends as they are.
    /// </summary>
    private sealed class ServerToolTracker(AgentToolSet tools)
    {
        private readonly Dictionary<string, PendingCall> _pendingCalls = new(StringComparer.Ordinal);

        // The streamed input of each content block of the current response, by block index.
        private readonly Dictionary<long, StringBuilder> _streamedInputs = [];

        // Server tool calls and results, such as a web search or the code execution the web tools filter results
        // with; graph tool calls and results are function contents, which the Anthropic client sends again itself.
        private static bool IsServerToolContent(AIContent content) =>
            content is (ToolCallContent and not FunctionCallContent) or (ToolResultContent and not FunctionResultContent);

        public void Observe(ChatResponseUpdate update)
        {
            long? stoppedBlockIndex = null;
            if (update.RawRepresentation is RawMessageStreamEvent streamEvent)
            {
                if (streamEvent.TryPickStart(out _))
                {
                    _streamedInputs.Clear();
                }
                else if (streamEvent.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickInputJson(out var inputJson))
                {
                    ref var input = ref CollectionsMarshal.GetValueRefOrAddDefault(_streamedInputs, delta.Index, out _);
                    (input ??= new StringBuilder()).Append(inputJson.PartialJson);
                }
                else if (streamEvent.TryPickContentBlockStop(out var stop))
                {
                    stoppedBlockIndex = stop.Index;
                }
            }

            foreach (var content in update.Contents)
            {
                if (IsServerToolContent(content))
                {
                    ObserveServerToolContent(content, stoppedBlockIndex);
                }
            }
        }

        private void ObserveServerToolContent(AIContent content, long? stoppedBlockIndex)
        {
            switch (content)
            {
                case WebSearchToolCallContent call when call.RawRepresentation is JsonModel block:
                    _pendingCalls[call.CallId] = new PendingCall(call, block.RawData, GetStreamedInput(stoppedBlockIndex));
                    break;
                case ToolCallContent call when call.RawRepresentation is JsonModel block:
                    // Not recorded: the code execution of the web tools is part of their calls.
                    var input = GetDeclaredInput(block.RawData, GetStreamedInput(stoppedBlockIndex)) ?? WriteObject(_ => { });
                    call.RawRepresentation = new ContentBlockParam(ToElement(block.RawData, replacedInput: input));
                    break;
                case ToolResultContent result when result.RawRepresentation is JsonModel block:
                    result.RawRepresentation = new ContentBlockParam(ToElement(block.RawData));
                    if (_pendingCalls.Remove(result.CallId, out var pendingCall))
                    {
                        Complete(pendingCall, block.RawData);
                    }

                    break;
            }
        }

        private JsonElement? GetStreamedInput(long? blockIndex)
        {
            if (blockIndex is not { } index || !_streamedInputs.TryGetValue(index, out var json))
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(json.ToString());
                return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>Records the calls still waiting for a result as failed, at the end of the turn.</summary>
        public void CompletePendingCalls()
        {
            foreach (var pendingCall in _pendingCalls.Values)
            {
                Complete(pendingCall, result: null);
            }

            _pendingCalls.Clear();
        }

        /// <summary>Gives the calls still waiting for a result their request block, without recording them.</summary>
        public void PreparePendingCallsForResend()
        {
            foreach (var pendingCall in _pendingCalls.Values)
            {
                var (call, block, streamedInput) = pendingCall;
                call.RawRepresentation = new ContentBlockParam(ToElement(block, replacedInput: GetInput(call, block, streamedInput, result: null)));
            }
        }

        private void Complete(PendingCall pendingCall, IReadOnlyDictionary<string, JsonElement>? result)
        {
            var (call, block, streamedInput) = pendingCall;
            var name = block.TryGetValue("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()!
                : "web_search";
            var input = GetInput(call, block, streamedInput, result);

            // A call sent with a continuation keeps the block it was sent with, so the next request repeats it unchanged.
            if (call.RawRepresentation is not ContentBlockParam)
            {
                call.RawRepresentation = new ContentBlockParam(ToElement(block, replacedInput: input));
            }

            var (preview, isError) = result is null ? ("No result.", true) : DescribeResult(result);
            tools.RecordBackendCall(name, input.GetRawText(), preview, isError);
        }

        // The streamed call block holds an empty input, so the input comes from the streamed input deltas.
        private static JsonElement? GetDeclaredInput(IReadOnlyDictionary<string, JsonElement> block, JsonElement? streamedInput)
        {
            if (block.TryGetValue("input", out var input) && input.ValueKind == JsonValueKind.Object && input.EnumerateObject().Any())
            {
                return input;
            }

            return streamedInput is { } streamed && streamed.EnumerateObject().Any() ? streamed : null;
        }

        // Without a declared input the query survives in the content, and the URL of a fetch in a successful result.
        private static JsonElement GetInput(
            WebSearchToolCallContent call,
            IReadOnlyDictionary<string, JsonElement> block,
            JsonElement? streamedInput,
            IReadOnlyDictionary<string, JsonElement>? result)
        {
            if (GetDeclaredInput(block, streamedInput) is { } declared)
            {
                return declared;
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

        private sealed record PendingCall(WebSearchToolCallContent Call, IReadOnlyDictionary<string, JsonElement> Block, JsonElement? StreamedInput);
    }
}
