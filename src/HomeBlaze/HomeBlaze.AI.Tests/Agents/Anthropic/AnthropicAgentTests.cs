using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
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

    /// <summary>When set, turns use the production Anthropic client over this handler instead of <see cref="Client"/>.</summary>
    public HttpMessageHandler? HttpHandler { get; set; }

    public int CreatedClientCount { get; private set; }

    protected override HttpClient HttpClient =>
        HttpHandler is { } handler ? new HttpClient(handler, disposeHandler: false) : base.HttpClient;

    protected override IChatClient CreateChatClient(string apiKey, int maxOutputTokens)
    {
        CreatedClientCount++;
        return HttpHandler is null ? Client : base.CreateChatClient(apiKey, maxOutputTokens);
    }
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
        var summary = Summarize();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, summary)));
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
    public async Task WhenTextPrecedesAToolCall_ThenOnlyTheFinalTextIsTheAnswer()
    {
        // Arrange
        await using var test = await StartAsync(agent =>
        {
            agent.AllowedTools = ["get_property"];
            agent.Client.Respond = (messages, _) => messages[^1].Contents.OfType<FunctionResultContent>().Any()
                ? [new ChatResponseUpdate(ChatRole.Assistant, "[quiet] nothing new")]
                :
                [
                    new ChatResponseUpdate(ChatRole.Assistant, "Let me check the temperature."),
                    new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("call1", "get_property", new Dictionary<string, object?> { ["path"] = "/Temperature" })])
                ];
        });

        // Act
        test.Agent.RunNow();
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.RunCount == 1);

        // Assert
        var run = test.Agent.Timeline.Entries.Single(entry => entry.Kind == AgentTimelineEntryKind.BackgroundRun);
        Assert.True(run.IsQuiet);
        Assert.Equal("[quiet] nothing new", run.Text);
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
        Assert.Equal(ReasoningEffort.Low, options.Reasoning?.Effort);
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
    public async Task WhenTheTurnIsCancelledWhileSummarizing_ThenTheAnswerReachesTheTimeline()
    {
        // Arrange
        await using var test = await StartAsync(agent => agent.HistoryTokenBudget = 1000);
        await test.Agent.SendMessageAsync("first", CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        test.Agent.Client.Respond = (_, _) => [new ChatResponseUpdate(ChatRole.Assistant, "Hello again"), Usage(5000)];
        test.Agent.Client.Summarize = () =>
        {
            cancellation.Cancel();
            return "summary";
        };

        // Act
        await test.Agent.SendMessageAsync("second", cancellation.Token);

        // Assert
        Assert.Single(test.Agent.Client.SummaryOptions);
        Assert.Equal(AgentTimelineEntryKind.Assistant, test.Agent.Timeline.Entries[^1].Kind);
        Assert.Equal("Hello again", test.Agent.Timeline.Entries[^1].Text);
        Assert.Contains("\"second\"", await File.ReadAllTextAsync(SessionPath(test)));
    }

    [Fact]
    public async Task WhenSummarizingFailed_ThenTheNextTurnDoesNotSummarizeAgain()
    {
        // Arrange
        await using var test = await StartAsync(agent =>
        {
            agent.HistoryTokenBudget = 1000;
            agent.Client.Respond = (_, _) => [new ChatResponseUpdate(ChatRole.Assistant, "Hello"), Usage(5000)];
            agent.Client.Summarize = () => throw new HttpRequestException("summary failed");
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
        var handler = new StubHandler(statusCode, ErrorBody(errorType, $"Request with key {ApiKey} failed"));
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
        var request = Assert.Single(handler.Requests);
        Assert.Equal(ApiKey, request.ApiKey);
        Assert.False(request.HasAuthorization);
    }

    [Fact]
    public async Task WhenTheRequestIsSent_ThenItEnablesPromptCachingAndUsesTheConfiguredModelAndLimit()
    {
        // Arrange
        var handler = new StubHandler(HttpStatusCode.InternalServerError, ErrorBody("api_error", "failed"));
        await using var test = await StartAsync(agent =>
        {
            agent.HttpHandler = handler;
            agent.MaxTokens = 3000;
        });

        // Act
        await test.Agent.SendMessageAsync("hello", CancellationToken.None);

        // Assert
        var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body).RootElement;
        Assert.Equal("ephemeral", body.GetProperty("cache_control").GetProperty("type").GetString());
        Assert.Equal("claude-sonnet-5", body.GetProperty("model").GetString());
        Assert.Equal(3000, body.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task WhenTheApiKeyChanges_ThenANewClientSendsTheNewKey()
    {
        // Arrange
        var handler = new StubHandler(HttpStatusCode.InternalServerError, ErrorBody("api_error", "failed"));
        await using var test = await StartAsync(agent => agent.HttpHandler = handler);
        await test.Agent.SendMessageAsync("first", CancellationToken.None);
        await test.Agent.SendMessageAsync("second", CancellationToken.None);

        // Act
        test.Agent.ApiKey = "sk-ant-other-key";
        await test.Agent.SendMessageAsync("third", CancellationToken.None);

        // Assert
        Assert.Equal(2, test.Agent.CreatedClientCount);
        Assert.Equal([ApiKey, ApiKey, "sk-ant-other-key"], handler.Requests.Select(request => request.ApiKey));
    }

    [Fact]
    public async Task WhenThePromptIsTooLong_ThenTheErrorPointsToANewConversation()
    {
        // Arrange
        var handler = new StubHandler(HttpStatusCode.BadRequest, ErrorBody("invalid_request_error", "prompt is too long: 210000 tokens > 200000 maximum"));
        await using var test = await StartAsync(agent => agent.HttpHandler = handler);

        // Act
        await test.Agent.SendMessageAsync("hello", CancellationToken.None);

        // Assert
        var entry = test.Agent.Timeline.Entries[^1];
        Assert.Equal(AgentTimelineEntryKind.Error, entry.Kind);
        Assert.Contains("new conversation", entry.Text);
        Assert.Equal(ServiceStatus.Error, test.Agent.Status);
    }

    [Fact]
    public async Task WhenTheStreamReportsAnError_ThenTheErrorMessageIsShown()
    {
        // Arrange
        var handler = new StubHandler(
            HttpStatusCode.OK,
            "event: error\ndata: " + ErrorBody("overloaded_error", "Overloaded") + "\n\n",
            "text/event-stream");
        await using var test = await StartAsync(agent => agent.HttpHandler = handler);

        // Act
        await test.Agent.SendMessageAsync("hello", CancellationToken.None);

        // Assert
        var entry = test.Agent.Timeline.Entries[^1];
        Assert.Equal(AgentTimelineEntryKind.Error, entry.Kind);
        Assert.Equal("The Anthropic API failed while answering: Overloaded", entry.Text);
        Assert.Equal(ServiceStatus.Error, test.Agent.Status);
    }

    private static string ErrorBody(string type, string message) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["type"] = "error",
            ["error"] = new Dictionary<string, string> { ["type"] = type, ["message"] = message }
        });

    private sealed record CapturedRequest(string? ApiKey, bool HasAuthorization, string Body);

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _body;
        private readonly string _mediaType;

        public StubHandler(HttpStatusCode statusCode, string body, string mediaType = "application/json")
        {
            _statusCode = statusCode;
            _body = body;
            _mediaType = mediaType;
        }

        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(
                request.Headers.TryGetValues("x-api-key", out var values) ? values.Single() : null,
                request.Headers.Authorization is not null,
                await request.Content!.ReadAsStringAsync(cancellationToken)));

            var response = new HttpResponseMessage(_statusCode) { Content = new StringContent(_body, System.Text.Encoding.UTF8, _mediaType) };
            // Keeps the SDK from retrying, which would wait between attempts.
            response.Headers.Add("x-should-retry", "false");
            return response;
        }
    }
}
