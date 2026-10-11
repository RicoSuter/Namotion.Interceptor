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
/// Runs one turn against the real, logged-in Claude CLI, which spends a small amount of the local subscription.
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
    public async Task WhenAskingForAPropertyValue_ThenTheRealCliCallsTheToolAndAnswers()
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

        // Assert
        foreach (var entry in test.Agent.Timeline.Entries)
        {
            _output.WriteLine($"{entry.Kind}: {entry.Text} (duration {entry.Duration}, tool calls: {string.Join(", ", entry.ToolCalls.Select(call => $"{call.Name}({call.Arguments}) error={call.IsError} result={call.ResultPreview}"))})");
        }

        _output.WriteLine($"Status: {test.Agent.Status}, message: {test.Agent.StatusMessage}");

        var reply = test.Agent.Timeline.Entries[^1];
        Assert.Equal(AgentTimelineEntryKind.Assistant, reply.Kind);
        Assert.Contains("21.5", reply.Text);
        Assert.Contains(reply.ToolCalls, call => call.Name == "get_property" && !call.IsError);
    }
}
