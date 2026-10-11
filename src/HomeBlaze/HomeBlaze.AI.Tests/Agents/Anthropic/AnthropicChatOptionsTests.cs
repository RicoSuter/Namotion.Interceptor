using System.Net;
using System.Text.Json;
using Anthropic;
using HomeBlaze.AI.Agents;
using HomeBlaze.AI.Agents.Anthropic;
using Microsoft.Extensions.AI;
using Namotion.Interceptor.Mcp;
using Xunit;

namespace HomeBlaze.AI.Tests.Agents.Anthropic;

public class AnthropicChatOptionsTests
{
    private const string Model = "claude-sonnet-5";

    [Theory]
    [InlineData(AgentEffort.Low, "low")]
    [InlineData(AgentEffort.Medium, "medium")]
    [InlineData(AgentEffort.High, "high")]
    [InlineData(AgentEffort.ExtraHigh, "xhigh")]
    [InlineData(AgentEffort.Max, "max")]
    public async Task WhenAnEffortIsSet_ThenTheRequestCarriesItAsOutputConfigEffort(AgentEffort effort, string expected)
    {
        // Arrange
        var handler = new CapturingHandler();
        using var client = CreateClient(handler);
        var options = AnthropicChatOptions.Create(Model, 1000, effort, []);

        // Act
        await Assert.ThrowsAnyAsync<Exception>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options));

        // Assert
        var body = JsonDocument.Parse(handler.Body!).RootElement;
        Assert.Equal(Model, body.GetProperty("model").GetString());
        Assert.Equal(1000, body.GetProperty("max_tokens").GetInt32());
        var message = Assert.Single(body.GetProperty("messages").EnumerateArray());
        Assert.Equal("user", message.GetProperty("role").GetString());
        Assert.Equal(expected, body.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal("adaptive", body.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("ephemeral", body.GetProperty("cache_control").GetProperty("type").GetString());
    }

    [Fact]
    public async Task WhenMaxEffortIsStreamedWithTools_ThenTheRequestCarriesTheChatMessagesToolsAndEffort()
    {
        // Arrange
        var handler = new CapturingHandler();
        using var client = CreateClient(handler);
        var options = AnthropicChatOptions.Create(Model, 1000, AgentEffort.Max, [CreateTool("browse")]);

        // Act
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.System, "SYSTEM"), new ChatMessage(ChatRole.User, "first"), new ChatMessage(ChatRole.Assistant, "reply"), new ChatMessage(ChatRole.User, "second")],
                options))
            {
                // Only the captured request matters; the stub handler answers with an error.
            }
        });

        // Assert
        var body = JsonDocument.Parse(handler.Body!).RootElement;
        Assert.True(body.GetProperty("stream").GetBoolean());
        Assert.Equal(Model, body.GetProperty("model").GetString());
        Assert.Equal(1000, body.GetProperty("max_tokens").GetInt32());
        Assert.Equal("SYSTEM", body.GetProperty("system")[0].GetProperty("text").GetString());
        Assert.Equal(["user", "assistant", "user"], body.GetProperty("messages").EnumerateArray().Select(message => message.GetProperty("role").GetString()));
        Assert.Equal("browse", Assert.Single(body.GetProperty("tools").EnumerateArray()).GetProperty("name").GetString());
        Assert.Equal("max", body.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal("ephemeral", body.GetProperty("cache_control").GetProperty("type").GetString());
    }

    [Fact]
    public async Task WhenNoEffortIsSet_ThenTheRequestHasNoEffortAndNoThinking()
    {
        // Arrange
        var handler = new CapturingHandler();
        using var client = CreateClient(handler);

        // Act
        await Assert.ThrowsAnyAsync<Exception>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], AnthropicChatOptions.Create(Model, 1000, null, [])));

        // Assert
        var body = JsonDocument.Parse(handler.Body!).RootElement;
        Assert.Equal(Model, body.GetProperty("model").GetString());
        Assert.Equal(1000, body.GetProperty("max_tokens").GetInt32());
        Assert.False(body.TryGetProperty("output_config", out var outputConfig) && outputConfig.TryGetProperty("effort", out _));
        Assert.False(body.TryGetProperty("thinking", out _));
        Assert.Equal("ephemeral", body.GetProperty("cache_control").GetProperty("type").GetString());
    }

    private static IChatClient CreateClient(HttpMessageHandler handler) =>
        new AnthropicClient { ApiKey = "test-key", HttpClient = new HttpClient(handler), MaxRetries = 0 }
            .AsIChatClient(defaultModelId: null, defaultMaxOutputTokens: 4000);

    private static McpToolInfo CreateTool(string name) => new()
    {
        Name = name,
        Description = "A test tool.",
        InputSchema = JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement,
        Handler = (_, _) => Task.FromResult<object?>(null)
    };

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
}
