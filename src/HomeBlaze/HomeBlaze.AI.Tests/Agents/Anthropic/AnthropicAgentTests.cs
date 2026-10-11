using System.Net;
using System.Runtime.CompilerServices;
using Anthropic;
using HomeBlaze.Abstractions;
using HomeBlaze.AI.Agents;
using HomeBlaze.AI.Agents.Anthropic;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Testing;
using Xunit;

namespace HomeBlaze.AI.Tests.Agents.Anthropic;

// A plain subclass, like FakeAgent: no intercepted properties of its own.
public class TestableAnthropicAgent : AnthropicAgent
{
    public TestableAnthropicAgent(AgentServices services) : base(services, NullLogger<AnthropicAgent>.Instance)
    {
    }

    public ScriptedChatClient Client { get; } = new();

    /// <summary>When set, turns use the real Anthropic client over this handler instead of <see cref="Client"/>.</summary>
    public HttpMessageHandler? HttpHandler { get; set; }

    protected override IChatClient CreateChatClient(string apiKey, int maxOutputTokens) =>
        HttpHandler is { } handler
            ? new AnthropicClient { ApiKey = apiKey, HttpClient = new HttpClient(handler, disposeHandler: false), MaxRetries = 0 }
                .AsIChatClient(defaultModelId: null, defaultMaxOutputTokens: maxOutputTokens)
            : Client;
}

public sealed class ScriptedChatClient : IChatClient
{
    public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

    public List<ChatOptions?> SummaryOptions { get; } = [];

    public Func<IReadOnlyList<ChatMessage>, ChatOptions?, IEnumerable<ChatResponseUpdate>> Respond { get; set; } =
        (_, _) => [new ChatResponseUpdate(ChatRole.Assistant, "Hello world")];

    public Func<string> Summarize { get; set; } = () => "summary";

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        SummaryOptions.Add(options);
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Summarize())));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        Requests.Add(list);
        foreach (var update in Respond(list, options))
        {
            await Task.Yield();
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

public class AnthropicAgentTests
{
    private const string ApiKey = "sk-ant-test-key-1234";

    private static Task<AgentTestContext<TestableAnthropicAgent>> StartAsync(Action<TestableAnthropicAgent>? configure = null) =>
        AgentTestContext<TestableAnthropicAgent>.StartAsync(
            services => new TestableAnthropicAgent(services),
            agent =>
            {
                agent.ApiKey = ApiKey;
                agent.Model = "claude-sonnet-5";
                configure?.Invoke(agent);
            });

    private static string SessionPath(AgentTestContext<TestableAnthropicAgent> test) =>
        Path.Combine(test.DataDirectory, "Agents", "Agent.session.json");

    private static ChatResponseUpdate Usage(long inputTokens) =>
        new(ChatRole.Assistant, [new UsageContent(new UsageDetails { InputTokenCount = inputTokens, OutputTokenCount = 10 })]);

    [Fact]
    public async Task WhenChatting_ThenTheHistoryIsSentOnTheNextTurnAndPersisted()
    {
        // Arrange
        await using var test = await StartAsync();

        // Act
        await test.Agent.SendMessageAsync("first", CancellationToken.None);
        await test.Agent.SendMessageAsync("second", CancellationToken.None);

        // Assert
        var second = test.Agent.Client.Requests[1];
        Assert.Equal(ChatRole.System, second[0].Role);
        Assert.Contains(second, message => message.Text == "first");
        Assert.Contains(second, message => message.Role == ChatRole.Assistant && message.Text == "Hello world");
        Assert.Equal("second", second[^1].Text);
        Assert.Contains("first", await File.ReadAllTextAsync(SessionPath(test)));
    }

    [Fact]
    public async Task WhenTheAgentRestarts_ThenTheHistoryFromTheSessionFileIsSent()
    {
        // Arrange
        await using var test = await StartAsync();
        await test.Agent.SendMessageAsync("first", CancellationToken.None);
        await test.Agent.StopAsync(CancellationToken.None);
        await test.Agent.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.Status == ServiceStatus.Running);

        // Act
        await test.Agent.SendMessageAsync("second", CancellationToken.None);

        // Assert
        Assert.Contains(test.Agent.Client.Requests[1], message => message.Text == "first");
    }

    [Fact]
    public async Task WhenTheSessionFileIsDamaged_ThenTheAgentStartsWithANewHistory()
    {
        // Arrange
        await using var test = await StartAsync();
        await test.Agent.StopAsync(CancellationToken.None);
        Directory.CreateDirectory(Path.GetDirectoryName(SessionPath(test))!);
        await File.WriteAllTextAsync(SessionPath(test), "{ damaged");
        await test.Agent.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.Status == ServiceStatus.Running);

        // Act
        await test.Agent.SendMessageAsync("hello", CancellationToken.None);

        // Assert
        Assert.Equal(2, test.Agent.Client.Requests[0].Count);
        Assert.Equal("Hello world", test.Agent.Timeline.Entries[^1].Text);
    }

    [Fact]
    public async Task WhenStartingANewConversation_ThenTheHistoryIsDiscarded()
    {
        // Arrange
        await using var test = await StartAsync();
        await test.Agent.SendMessageAsync("first", CancellationToken.None);

        // Act
        await test.Agent.NewConversationAsync(CancellationToken.None);
        await test.Agent.SendMessageAsync("second", CancellationToken.None);

        // Assert
        var second = test.Agent.Client.Requests[1];
        Assert.Equal(2, second.Count);
        Assert.DoesNotContain(second, message => message.Text == "first");
    }

    [Fact]
    public async Task WhenTheModelCallsATool_ThenTheToolRunsAndTheCallIsRecorded()
    {
        // Arrange
        await using var test = await StartAsync(agent =>
        {
            agent.AllowedTools = ["get_property"];
            agent.Client.Respond = (messages, _) => messages[^1].Contents.OfType<FunctionResultContent>().Any()
                ? [new ChatResponseUpdate(ChatRole.Assistant, "It is 21.5.")]
                : [new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("call1", "get_property", new Dictionary<string, object?> { ["path"] = "/Temperature" })])];
        });
        test.Root.Temperature = 21.5m;

        // Act
        await test.Agent.SendMessageAsync("How warm is it?", CancellationToken.None);

        // Assert
        var reply = test.Agent.Timeline.Entries[^1];
        Assert.Equal("It is 21.5.", reply.Text);
        var call = Assert.Single(reply.ToolCalls);
        Assert.Equal("get_property", call.Name);
        Assert.Contains("21.5", call.ResultPreview);
    }

    [Fact]
    public async Task WhenNoApiKeyIsSet_ThenTheAgentIsUnavailable()
    {
        // Arrange
        await using var test = await StartAsync(agent => agent.ApiKey = null);

        // Act
        test.Agent.RunNow();
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.Status == ServiceStatus.Unavailable);

        // Assert
        Assert.Contains("API key", test.Agent.StatusMessage);
    }

    [Fact]
    public async Task WhenRunningInTheBackground_ThenNoHistoryIsKept()
    {
        // Arrange
        await using var test = await StartAsync();

        // Act
        test.Agent.RunNow();
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.RunCount == 1);
        await test.Agent.SendMessageAsync("chat", CancellationToken.None);

        // Assert
        var chat = test.Agent.Client.Requests[1];
        Assert.Equal(2, chat.Count);
        Assert.Contains("Background findings", chat[1].Text);
        Assert.DoesNotContain(chat, message => message.Text.Contains("Trigger:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhenTheInputExceedsTheHistoryBudget_ThenTheOlderHalfIsSummarizedAndPersisted()
    {
        // Arrange
        await using var test = await StartAsync(agent =>
        {
            agent.HistoryTokenBudget = 1000;
            agent.MaxTokens = 20000;
        });
        await test.Agent.SendMessageAsync("first", CancellationToken.None);
        test.Agent.Client.Respond = (_, _) => [new ChatResponseUpdate(ChatRole.Assistant, "Hello again"), Usage(5000)];

        // Act
        await test.Agent.SendMessageAsync("second", CancellationToken.None);

        // Assert
        var options = Assert.Single(test.Agent.Client.SummaryOptions);
        Assert.Equal("claude-sonnet-5", options!.ModelId);
        Assert.Equal(4096, options.MaxOutputTokens);
        Assert.Null(options.Reasoning);
        var session = await File.ReadAllTextAsync(SessionPath(test));
        Assert.Contains("\"summary\":\"summary\"", session);
        Assert.DoesNotContain("\"first\"", session);
        Assert.Equal("Hello again", test.Agent.Timeline.Entries[^1].Text);
        Assert.Equal(5000, test.Agent.Timeline.Entries[^1].Usage!.InputTokens);
    }

    [Fact]
    public async Task WhenACompactionCouldNotGetBelowTheBudget_ThenTheNextTurnDoesNotSummarizeAgain()
    {
        // Arrange
        await using var test = await StartAsync(agent =>
        {
            agent.HistoryTokenBudget = 1000;
            agent.Client.Respond = (_, _) => [new ChatResponseUpdate(ChatRole.Assistant, "Hello"), Usage(5000)];
        });
        await test.Agent.SendMessageAsync("first", CancellationToken.None);
        await test.Agent.SendMessageAsync("second", CancellationToken.None);

        // Act
        await test.Agent.SendMessageAsync("third", CancellationToken.None);

        // Assert
        Assert.Single(test.Agent.Client.SummaryOptions);
        Assert.Equal("Hello", test.Agent.Timeline.Entries[^1].Text);
    }

    [Fact]
    public async Task WhenSummarizingFails_ThenTheTurnSucceedsAndTheHistoryIsKept()
    {
        // Arrange
        await using var test = await StartAsync(agent => agent.HistoryTokenBudget = 1000);
        await test.Agent.SendMessageAsync("first", CancellationToken.None);
        test.Agent.Client.Respond = (_, _) => [new ChatResponseUpdate(ChatRole.Assistant, "Hello again"), Usage(5000)];
        test.Agent.Client.Summarize = () => throw new HttpRequestException("summary failed");

        // Act
        await test.Agent.SendMessageAsync("second", CancellationToken.None);

        // Assert
        Assert.Equal(AgentTimelineEntryKind.Assistant, test.Agent.Timeline.Entries[^1].Kind);
        Assert.Equal("Hello again", test.Agent.Timeline.Entries[^1].Text);
        var session = await File.ReadAllTextAsync(SessionPath(test));
        Assert.Contains("\"first\"", session);
        Assert.Contains("\"second\"", session);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "authentication_error", ServiceStatus.Unavailable, "rejected the API key")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate_limit_error", ServiceStatus.Unavailable, "rate limit")]
    [InlineData(HttpStatusCode.InternalServerError, "api_error", ServiceStatus.Error, "failed")]
    [InlineData((HttpStatusCode)529, "overloaded_error", ServiceStatus.Error, "failed")]
    public async Task WhenTheApiFails_ThenTheFailureIsClassifiedAndTheKeyIsNotShown(
        HttpStatusCode statusCode, string errorType, ServiceStatus expectedStatus, string expectedText)
    {
        // Arrange
        var handler = new StatusCodeHandler(
            statusCode,
            $$$"""{"type":"error","error":{"type":"{{{errorType}}}","message":"Request with key {{{ApiKey}}} failed"}}""");
        await using var test = await StartAsync(agent => agent.HttpHandler = handler);

        // Act
        await test.Agent.SendMessageAsync("hello", CancellationToken.None);

        // Assert
        var entry = test.Agent.Timeline.Entries[^1];
        Assert.Equal(AgentTimelineEntryKind.Error, entry.Kind);
        Assert.Contains(expectedText, entry.Text);
        Assert.DoesNotContain(ApiKey, entry.Text);
        Assert.DoesNotContain(ApiKey, test.Agent.StatusMessage);
        Assert.Equal(expectedStatus, test.Agent.Status);
        Assert.Equal(ApiKey, handler.SentApiKey);
    }

    private sealed class StatusCodeHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _body;

        public StatusCodeHandler(HttpStatusCode statusCode, string body)
        {
            _statusCode = statusCode;
            _body = body;
        }

        public string? SentApiKey { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SentApiKey = request.Headers.TryGetValues("x-api-key", out var values) ? values.Single() : null;
            return Task.FromResult(new HttpResponseMessage(_statusCode) { Content = new StringContent(_body) });
        }
    }
}
