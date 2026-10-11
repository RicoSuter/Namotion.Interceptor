using System.Text.Json;
using HomeBlaze.AI.Agents.Anthropic;
using Microsoft.Extensions.AI;
using Xunit;

namespace HomeBlaze.AI.Tests.Agents.Anthropic;

public class AnthropicHistoryTests
{
    private static IEnumerable<ChatMessage> ToolExchange(int index) =>
    [
        new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call" + index, "browse", new Dictionary<string, object?> { ["path"] = "/" })]),
        new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call" + index, "root")]),
        new ChatMessage(ChatRole.Assistant, "answer " + index)
    ];

    private static AnthropicHistory CreateHistory(int turns)
    {
        var history = new AnthropicHistory();
        for (var index = 0; index < turns; index++)
        {
            history.Append(new ChatMessage(ChatRole.User, "question " + index), ToolExchange(index));
        }

        return history;
    }

    [Fact]
    public void WhenBuildingRequestMessages_ThenSystemSummaryHistoryAndNewMessageAreInOrder()
    {
        // Arrange
        var history = CreateHistory(1);
        history.RestoreSummary("earlier things");

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
    public async Task WhenCompacting_ThenTheOlderHalfIsSummarizedAndToolPairsStayTogether()
    {
        // Arrange
        var history = CreateHistory(4);
        var summarizer = new SummarizingClient("summary of 0 and 1");
        var options = new ChatOptions { ModelId = "claude-sonnet-5", MaxOutputTokens = 2000 };

        // Act
        await history.CompactAsync(summarizer, options, CancellationToken.None);

        // Assert
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
    public async Task WhenCompactingAgain_ThenTheEarlierSummaryIsPartOfTheTranscript()
    {
        // Arrange
        var history = CreateHistory(4);
        history.RestoreSummary("the very first things");
        var summarizer = new SummarizingClient("new summary");

        // Act
        await history.CompactAsync(summarizer, cancellationToken: CancellationToken.None);

        // Assert
        Assert.Contains("the very first things", summarizer.LastPrompt);
        Assert.Equal("new summary", history.Summary);
    }

    [Fact]
    public async Task WhenTheSummaryIsEmpty_ThenTheHistoryIsKept()
    {
        // Arrange
        var history = CreateHistory(4);

        // Act
        await history.CompactAsync(new SummarizingClient(" "), cancellationToken: CancellationToken.None);

        // Assert
        Assert.Null(history.Summary);
        Assert.Equal(16, history.Messages.Count);
    }

    [Fact]
    public async Task WhenNoPlainUserMessageAllowsASplit_ThenNoModelCallIsMade()
    {
        // Arrange
        var history = CreateHistory(1);
        var summarizer = new SummarizingClient("unused");

        // Act
        await history.CompactAsync(summarizer, cancellationToken: CancellationToken.None);

        // Assert
        Assert.Null(summarizer.LastOptions);
        Assert.Equal(string.Empty, summarizer.LastPrompt);
        Assert.Equal(4, history.Messages.Count);
    }

    [Fact]
    public void WhenSerialized_ThenToolCallsAndSummaryRoundTrip()
    {
        // Arrange
        var history = CreateHistory(1);
        history.RestoreSummary("earlier");

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
        Assert.Equal("root", result.Result?.ToString());
        Assert.Equal(ChatRole.Tool, restored.Messages[2].Role);
        Assert.Equal("answer 0", restored.Messages[3].Text);
    }

    [Fact]
    public void WhenTheContentIsNotAHistory_ThenDeserializeThrowsAJsonException()
    {
        // Act & Assert
        Assert.ThrowsAny<JsonException>(() => AnthropicHistory.Deserialize("[1, 2]"));
        Assert.ThrowsAny<JsonException>(() => AnthropicHistory.Deserialize("{ broken"));
    }

    private sealed class SummarizingClient : IChatClient
    {
        private readonly string _summary;

        public SummarizingClient(string summary) => _summary = summary;

        public string LastPrompt { get; private set; } = string.Empty;

        public ChatOptions? LastOptions { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            LastPrompt = string.Join('\n', messages.Select(message => message.Text));
            LastOptions = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _summary)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
