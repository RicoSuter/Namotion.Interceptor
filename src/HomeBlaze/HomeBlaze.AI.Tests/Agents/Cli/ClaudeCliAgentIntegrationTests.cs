using HomeBlaze.AI.Agents;
using HomeBlaze.AI.Agents.Cli;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace HomeBlaze.AI.Tests.Agents.Cli;

/// <summary>
/// Runs two turns against the real, logged-in Claude CLI, the second resuming the session of the first, which spends a
/// small amount of the local subscription.
/// </summary>
[Trait("Category", "Integration")]
public class ClaudeCliAgentIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public ClaudeCliAgentIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [ClaudeCliFact]
    public async Task WhenAskingForAPropertyValueAndThenAFollowUp_ThenTheRealCliCallsTheToolAndAnswersBothInOneSession()
    {
        // Arrange
        var registry = new AgentTurnTokenRegistry();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(registry);
        builder.Services.AddHomeBlazeAgents();
        builder.Services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult { Tools = [] }))
            .WithCallToolHandler((_, _) => ValueTask.FromResult(new CallToolResult { IsError = true, Content = [] }));
        await using var app = builder.Build();
        app.MapHomeBlazeAgentMcp();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

        await using var test = await AgentTestContext<ClaudeCliAgent>.StartAsync(
            services => new ClaudeCliAgent(services, NullLogger<ClaudeCliAgent>.Instance),
            agent =>
            {
                agent.Model = "sonnet";
                agent.AllowedTools = ["get_property"];
                agent.Instructions = "Answer with the numeric value only.";
            },
            new Uri(address + AgentMcpEndpoint.Route),
            registry);
        test.Root.Temperature = 21.5m;

        // Act
        await test.Agent.SendMessageAsync("What is the value of /Temperature? Use the get_property tool.", CancellationToken.None);
        var firstReply = test.Agent.Timeline.Entries[^1];
        await test.Agent.SendMessageAsync("And the value plus 1?", CancellationToken.None);

        // Assert
        test.WriteTimeline(_output);

        Assert.Equal(AgentTimelineEntryKind.Assistant, firstReply.Kind);
        Assert.Contains("21.5", firstReply.Text);
        Assert.Contains(firstReply.ToolCalls, call => call.Name == "get_property" && !call.IsError);

        // Answered from the resumed session, which holds the value of the first turn.
        var secondReply = test.Agent.Timeline.Entries[^1];
        Assert.Equal(AgentTimelineEntryKind.Assistant, secondReply.Kind);
        Assert.Contains("22.5", secondReply.Text);
        Assert.DoesNotContain(test.Agent.Timeline.Entries, entry => entry.Kind is AgentTimelineEntryKind.Error or AgentTimelineEntryKind.Divider);
    }
}
