using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using HomeBlaze.AI.Agents.Anthropic;
using Microsoft.Extensions.AI;
using Xunit;

namespace HomeBlaze.AI.Tests.Agents.Anthropic;

public class AnthropicHistoryTests
{
    private static IEnumerable<ChatMessage> ToolExchange(int index, string answer = "answer ") =>
    [
        new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call" + index, "browse", new Dictionary<string, object?> { ["path"] = "/" })]),
        new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call" + index, "root")]),
        new ChatMessage(ChatRole.Assistant, answer + index)
    ];

    private static AnthropicHistory CreateHistory(int turns, Func<int, string>? answer = null)
    {
        var history = new AnthropicHistory();
        for (var index = 0; index < turns; index++)
        {
            history.Append(new ChatMessage(ChatRole.User, "question " + index), ToolExchange(index, answer?.Invoke(index) ?? "answer "));
        }

        return history;
    }

    // A summary only enters a history through compaction or a restored session.
    private static AnthropicHistory WithSummary(AnthropicHistory history, string summary)
    {
        var json = JsonNode.Parse(history.Serialize())!;
        json["summary"] = summary;
        return AnthropicHistory.Deserialize(json.ToJsonString());
    }

    [Fact]
    public void WhenBuildingRequestMessages_ThenSystemSummaryHistoryAndNewMessageAreInOrder()
    {
        // Arrange
        var history = WithSummary(CreateHistory(1), "earlier things");

        // Act
        var messages = history.BuildRequestMessages("SYSTEM", "new question");

        // Assert
        Assert.Equal(ChatRole.System, messages[0].Role);
        Assert.Equal("SYSTEM", messages[0].Text);
        Assert.Contains("earlier things", messages[1].Text);
        Assert.Equal("question 0", messages[2].Text);
        Assert.Equal("new question", messages[^1].Text);
        Assert.Equal(7, messages.Count);
    }

    [Fact]
    public void WhenAppendingAResponseWithReasoning_ThenTheReasoningIsNotKept()
    {
        // Arrange
        var history = new AnthropicHistory();

        // Act
        history.Append(new ChatMessage(ChatRole.User, "question"),
        [
            new ChatMessage(ChatRole.Assistant,
            [
                new TextReasoningContent("thinking about it") { ProtectedData = "signature" },
                new FunctionCallContent("call1", "browse", new Dictionary<string, object?> { ["path"] = "/" })
            ]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call1", "root")]),
            new ChatMessage(ChatRole.Assistant, [new TextReasoningContent(string.Empty) { ProtectedData = "redacted" }]),
            new ChatMessage(ChatRole.Assistant, [new TextReasoningContent("more thinking"), new TextContent("the answer")])
        ]);

        // Assert
        Assert.Equal(4, history.Messages.Count);
        Assert.DoesNotContain(history.Messages.SelectMany(message => message.Contents), content => content is TextReasoningContent);
        Assert.IsType<FunctionCallContent>(Assert.Single(history.Messages[1].Contents));
        Assert.Equal("the answer", history.Messages[3].Text);
    }

    [Fact]
    public async Task WhenRestored_ThenTheRequestMessagesAreTheSameAsBefore()
    {
        // Arrange
        var history = new AnthropicHistory();
        history.Append(new ChatMessage(ChatRole.User, "Grüße, how warm is it?"),
        [
            new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent("call1", "get_property", new Dictionary<string, object?> { ["path"] = "/Temperature" }),
                new FunctionCallContent("call2", "browse", new Dictionary<string, object?> { ["path"] = "/" }),
                new FunctionCallContent("call3", "set_property", new Dictionary<string, object?> { ["path"] = "/Mode" })
            ]),
            new ChatMessage(ChatRole.Tool,
            [
                new FunctionResultContent("call1", new Dictionary<string, object?> { ["value"] = 21.5, ["unit"] = "°C" }),
                new FunctionResultContent("call2", "root"),
                new FunctionResultContent("call3", "Error: Function failed.") { Exception = new InvalidOperationException("failed") }
            ]),
            new ChatMessage(ChatRole.Assistant, "It is 21.5 °C.")
        ]);

        // Act
        var restored = AnthropicHistory.Deserialize(history.Serialize());
        var original = await CaptureRequestMessagesAsync(history);
        var afterRestore = await CaptureRequestMessagesAsync(restored);

        // Assert
        Assert.Equal(original, afterRestore);
        var results = restored.Messages[2].Contents.Cast<FunctionResultContent>().ToList();
        Assert.All(results, result => Assert.IsType<string>(result.Result));
        var value = JsonDocument.Parse((string)results[0].Result!).RootElement;
        Assert.Equal(21.5, value.GetProperty("value").GetDouble());
        Assert.Equal("°C", value.GetProperty("unit").GetString());
        var toolResults = JsonDocument.Parse(afterRestore).RootElement.EnumerateArray()
            .Where(message => message.GetProperty("content").ValueKind == JsonValueKind.Array)
            .SelectMany(message => message.GetProperty("content").EnumerateArray())
            .Where(block => block.GetProperty("type").GetString() == "tool_result")
            .ToList();
        Assert.Equal(3, toolResults.Count);
        Assert.True(toolResults[2].GetProperty("is_error").GetBoolean());
        Assert.False(toolResults[1].GetProperty("is_error").GetBoolean());
    }

    [Fact]
    public void WhenSerialized_ThenNonAsciiTextIsNotEscaped()
    {
        // Arrange
        var history = new AnthropicHistory();
        history.Append(new ChatMessage(ChatRole.User, "Grüße"), [new ChatMessage(ChatRole.Assistant, "Hallo")]);

        // Act
        var json = history.Serialize();

        // Assert
        Assert.Contains("Grüße", json);
    }

    [Fact]
    public async Task WhenCompacting_ThenTheOlderHalfIsSummarizedAndToolPairsStayTogether()
    {
        // Arrange
        var history = CreateHistory(4);
        var summarizer = new SummarizingClient("summary of 0 and 1");
        var options = new ChatOptions { ModelId = "claude-sonnet-5", MaxOutputTokens = 2000 };

        // Act
        var outcome = await history.CompactAsync(summarizer, options, cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(AnthropicCompactionOutcome.Compacted, outcome);
        Assert.Equal("summary of 0 and 1", history.Summary);
        Assert.Equal("question 2", history.Messages[0].Text);
        Assert.Equal(8, history.Messages.Count);
        Assert.Contains("question 0", summarizer.LastPrompt);
        Assert.Contains("browse", summarizer.LastPrompt);
        Assert.Contains("root", summarizer.LastPrompt);
        Assert.DoesNotContain("question 2", summarizer.LastPrompt);
        Assert.Same(options, summarizer.LastOptions);
    }

    [Fact]
    public async Task WhenAnEarlyTurnIsLarge_ThenTheSplitFollowsTheSizeNotTheMessageCount()
    {
        // Arrange
        var history = CreateHistory(4, index => index == 0 ? new string('x', 1000) : "answer ");

        // Act
        await history.CompactAsync(new SummarizingClient("summary"), cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal("question 1", history.Messages[0].Text);
        Assert.Equal(12, history.Messages.Count);
    }

    [Fact]
    public async Task WhenTheLastTurnAloneIsLargerThanTheShareToKeep_ThenEverythingButTheLastTurnIsSummarized()
    {
        // Arrange
        var history = CreateHistory(4, index => index == 3 ? new string('x', 1000) : "answer ");

        // Act
        await history.CompactAsync(new SummarizingClient("summary"), keepFraction: 0.5, cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal("question 3", history.Messages[0].Text);
        Assert.Equal(4, history.Messages.Count);
    }

    [Fact]
    public async Task WhenCompactingAgain_ThenTheEarlierSummaryIsPartOfTheTranscript()
    {
        // Arrange
        var history = WithSummary(CreateHistory(4), "the very first things");
        var summarizer = new SummarizingClient("new summary");

        // Act
        await history.CompactAsync(summarizer, cancellationToken: CancellationToken.None);

        // Assert
        Assert.Contains("the very first things", summarizer.LastPrompt);
        Assert.Equal("new summary", history.Summary);
    }

    [Theory]
    [InlineData(" ", null)]
    [InlineData("a summary that was cut", "length")]
    public async Task WhenTheSummaryIsEmptyOrCutOff_ThenTheHistoryIsKept(string summary, string? finishReason)
    {
        // Arrange
        var history = CreateHistory(4);
        var summarizer = new SummarizingClient(summary)
        {
            FinishReason = finishReason is null ? null : new ChatFinishReason(finishReason)
        };

        // Act
        var outcome = await history.CompactAsync(summarizer, cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(AnthropicCompactionOutcome.SummaryIncomplete, outcome);
        Assert.Null(history.Summary);
        Assert.Equal(16, history.Messages.Count);
    }

    [Fact]
    public async Task WhenTheSummarizerThrows_ThenTheHistoryIsKept()
    {
        // Arrange
        var history = WithSummary(CreateHistory(4), "earlier");
        var summarizer = new SummarizingClient("unused") { Exception = new HttpRequestException("unavailable") };

        // Act & Assert
        await Assert.ThrowsAsync<HttpRequestException>(() => history.CompactAsync(summarizer, cancellationToken: CancellationToken.None));
        Assert.Equal("earlier", history.Summary);
        Assert.Equal(16, history.Messages.Count);
    }

    [Fact]
    public async Task WhenNoPlainUserMessageAllowsASplit_ThenNoModelCallIsMade()
    {
        // Arrange
        var history = CreateHistory(1);
        var summarizer = new SummarizingClient("unused");

        // Act
        var outcome = await history.CompactAsync(summarizer, cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(AnthropicCompactionOutcome.NothingToCompact, outcome);
        Assert.Null(summarizer.LastOptions);
        Assert.Equal(string.Empty, summarizer.LastPrompt);
        Assert.Equal(4, history.Messages.Count);
    }

    [Fact]
    public void WhenSerialized_ThenToolCallsAndSummaryRoundTrip()
    {
        // Arrange
        var history = WithSummary(CreateHistory(1), "earlier");

        // Act
        var restored = AnthropicHistory.Deserialize(history.Serialize());

        // Assert
        Assert.Equal("earlier", restored.Summary);
        Assert.Equal(4, restored.Messages.Count);
        var call = Assert.IsType<FunctionCallContent>(Assert.Single(restored.Messages[1].Contents));
        Assert.Equal("call0", call.CallId);
        Assert.Equal("browse", call.Name);
        Assert.Equal("/", call.Arguments!["path"]?.ToString());
        var result = Assert.IsType<FunctionResultContent>(Assert.Single(restored.Messages[2].Contents));
        Assert.Equal("call0", result.CallId);
        Assert.Equal("root", Assert.IsType<string>(result.Result));
        Assert.Equal(ChatRole.Tool, restored.Messages[2].Role);
        Assert.Equal("answer 0", restored.Messages[3].Text);
    }

    [Fact]
    public void WhenTheStoredMessagesContainNull_ThenTheNullEntriesAreSkipped()
    {
        // Arrange
        var json = CreateHistory(1).Serialize().Replace("\"messages\":[", "\"messages\":[null,", StringComparison.Ordinal);

        // Act
        var restored = AnthropicHistory.Deserialize(json);

        // Assert
        Assert.Equal(4, restored.Messages.Count);
        Assert.Equal("question 0", restored.Messages[0].Text);
    }

    [Fact]
    public void WhenTheContentIsNotAHistory_ThenDeserializeThrowsAJsonException()
    {
        // Act & Assert
        Assert.ThrowsAny<JsonException>(() => AnthropicHistory.Deserialize("[1, 2]"));
        Assert.ThrowsAny<JsonException>(() => AnthropicHistory.Deserialize("{ broken"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    public void WhenAStoredRoleIsInvalid_ThenDeserializeThrowsAJsonException(string role)
    {
        // Arrange
        var json = CreateHistory(1).Serialize();
        Assert.Contains("\"role\":\"user\"", json);
        json = json.Replace("\"role\":\"user\"", "\"role\":" + role, StringComparison.Ordinal);

        // Act & Assert
        Assert.ThrowsAny<JsonException>(() => AnthropicHistory.Deserialize(json));
    }

    private static async Task<string> CaptureRequestMessagesAsync(AnthropicHistory history)
    {
        var handler = new CapturingHandler();
        var client = new AnthropicClient { ApiKey = "test-key", HttpClient = new HttpClient(handler), MaxRetries = 0 }
            .AsIChatClient(defaultModelId: "claude-sonnet-5", defaultMaxOutputTokens: 1000);
        await Assert.ThrowsAnyAsync<Exception>(() => client.GetResponseAsync(history.BuildRequestMessages("SYSTEM", "next")));
        return JsonDocument.Parse(handler.Body!).RootElement.GetProperty("messages").GetRawText();
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.NotNull(request.Content);
            Body = await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"type":"error","error":{"type":"invalid_request_error","message":"captured"}}""")
            };
        }
    }

    private sealed class SummarizingClient : IChatClient
    {
        private readonly string _summary;

        public SummarizingClient(string summary) => _summary = summary;

        public string LastPrompt { get; private set; } = string.Empty;

        public ChatOptions? LastOptions { get; private set; }

        public ChatFinishReason? FinishReason { get; init; }

        public Exception? Exception { get; init; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            LastPrompt = string.Join('\n', messages.Select(message => message.Text));
            LastOptions = options;
            if (Exception is not null)
            {
                throw Exception;
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _summary)) { FinishReason = FinishReason });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
