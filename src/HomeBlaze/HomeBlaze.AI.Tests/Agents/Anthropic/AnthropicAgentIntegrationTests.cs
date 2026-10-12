using HomeBlaze.AI.Agents;
using HomeBlaze.AI.Agents.Anthropic;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace HomeBlaze.AI.Tests.Agents.Anthropic;

/// <summary>
/// Runs one turn against the real Anthropic API, which spends a small amount of API credits.
/// </summary>
[Trait("Category", "Integration")]
public class AnthropicAgentIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public AnthropicAgentIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [AnthropicApiFact]
    public async Task WhenAskingForAPropertyValue_ThenTheApiCallsTheToolAndAnswers()
    {
        // Arrange
        await using var test = await AgentTestContext<AnthropicAgent>.StartAsync(
            services => new AnthropicAgent(services, NullLogger<AnthropicAgent>.Instance),
            agent =>
            {
                agent.ApiKey = Environment.GetEnvironmentVariable(AnthropicApiFactAttribute.ApiKeyVariable);
                agent.Model = "claude-sonnet-5";
                agent.Effort = AgentEffort.Low;
                agent.AllowedTools = ["get_property"];
                agent.Instructions = "Answer with the numeric value only.";
            });
        test.Root.Temperature = 21.5m;

        // Act
        await test.Agent.SendMessageAsync("What is the value of /Temperature? Use the get_property tool.", CancellationToken.None);

        // Assert
        test.WriteTimeline(_output);

        var reply = test.Agent.Timeline.Entries[^1];
        Assert.Equal(AgentTimelineEntryKind.Assistant, reply.Kind);
        Assert.Contains("21.5", reply.Text);
        Assert.Contains(reply.ToolCalls, call => call.Name == "get_property" && !call.IsError);
    }
}
