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
        var handler = new AnthropicStubHandler(HttpStatusCode.BadRequest, AnthropicStubHandler.ErrorBody("invalid_request_error", "captured"));
        using var client = CreateClient(handler);
        var options = AnthropicChatOptions.Create(Model, 1000, effort, []);

        // Act
        await Assert.ThrowsAnyAsync<Exception>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options));

        // Assert
        var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body).RootElement;
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
        var handler = new AnthropicStubHandler(HttpStatusCode.BadRequest, AnthropicStubHandler.ErrorBody("invalid_request_error", "captured"));
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
        var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body).RootElement;
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
        var handler = new AnthropicStubHandler(HttpStatusCode.BadRequest, AnthropicStubHandler.ErrorBody("invalid_request_error", "captured"));
        using var client = CreateClient(handler);

        // Act
        await Assert.ThrowsAnyAsync<Exception>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], AnthropicChatOptions.Create(Model, 1000, null, [])));

        // Assert
        var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body).RootElement;
        Assert.Equal(Model, body.GetProperty("model").GetString());
        Assert.Equal(1000, body.GetProperty("max_tokens").GetInt32());
        Assert.False(body.TryGetProperty("output_config", out var outputConfig) && outputConfig.TryGetProperty("effort", out _));
        Assert.False(body.TryGetProperty("thinking", out _));
        Assert.Equal("ephemeral", body.GetProperty("cache_control").GetProperty("type").GetString());
    }

    [Fact]
    public async Task WhenWebToolsAreAllowed_ThenTheRequestCarriesTheServerToolsWithTheUseLimit()
    {
        // Arrange
        var handler = new AnthropicStubHandler(HttpStatusCode.BadRequest, AnthropicStubHandler.ErrorBody("invalid_request_error", "captured"));
        using var client = CreateClient(handler);
        var options = AnthropicChatOptions.WithBackendToolUses(
            AnthropicChatOptions.Create(Model, 1000, null, [CreateTool("browse")]), ["web_search", "web_fetch"], maxBackendToolUses: 7);

        // Act
        await Assert.ThrowsAnyAsync<Exception>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options));

        // Assert
        var tools = JsonDocument.Parse(Assert.Single(handler.Requests).Body).RootElement.GetProperty("tools").EnumerateArray().ToList();
        Assert.Equal(3, tools.Count);
        Assert.Equal("browse", tools[0].GetProperty("name").GetString());
        Assert.Equal("""{"name":"web_search","type":"web_search_20260209","max_uses":7}""", tools[1].GetRawText());
        Assert.Equal("""{"name":"web_fetch","type":"web_fetch_20260209","max_uses":7}""", tools[2].GetRawText());
    }

    [Fact]
    public async Task WhenNoWebToolIsAllowed_ThenTheRequestCarriesNoServerTool()
    {
        // Arrange
        var handler = new AnthropicStubHandler(HttpStatusCode.BadRequest, AnthropicStubHandler.ErrorBody("invalid_request_error", "captured"));
        using var client = CreateClient(handler);
        var options = AnthropicChatOptions.WithBackendToolUses(AnthropicChatOptions.Create(Model, 1000, null, [CreateTool("browse")]), [], maxBackendToolUses: 7);

        // Act
        await Assert.ThrowsAnyAsync<Exception>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options));

        // Assert
        var tool = Assert.Single(JsonDocument.Parse(Assert.Single(handler.Requests).Body).RootElement.GetProperty("tools").EnumerateArray());
        Assert.Equal("browse", tool.GetProperty("name").GetString());
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
}
