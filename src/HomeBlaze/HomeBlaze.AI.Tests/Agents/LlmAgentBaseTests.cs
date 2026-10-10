using System.Text.Json;
using HomeBlaze.Abstractions;
using HomeBlaze.AI.Agents;
using Namotion.Interceptor.Testing;
using Xunit;

namespace HomeBlaze.AI.Tests.Agents;

public class LlmAgentBaseTests
{
    private static Task<AgentTestContext<FakeAgent>> StartAsync(Action<FakeAgent>? configure = null) =>
        AgentTestContext<FakeAgent>.StartAsync(services => new FakeAgent(services), configure);

    [Fact]
    public async Task WhenARunIsRequestedManually_ThenABackgroundTurnRunsWithTheBackgroundModelAndWatchedState()
    {
        // Arrange
        await using var test = await StartAsync(agent =>
        {
            agent.Model = "chat-model";
            agent.BackgroundModel = "background-model";
            agent.BackgroundEffort = AgentEffort.Low;
            agent.WatchPaths = ["/Temperature"];
            agent.Instructions = "Watch the temperature.";
        });

        // Act
        test.Agent.RunNow();
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.RunCount == 1);

        // Assert
        var request = Assert.Single(test.Agent.Requests);
        Assert.Equal(AgentTurnKind.Background, request.Kind);
        Assert.Equal("background-model", request.Model);
        Assert.Equal(AgentEffort.Low, request.Effort);
        Assert.Contains("Trigger: manual", request.UserMessage);
        Assert.Contains("## /Temperature", request.UserMessage);
        Assert.Contains("Watch the temperature.", request.SystemPrompt);
        var entry = Assert.Single(test.Agent.Timeline.Entries);
        Assert.Equal(AgentTimelineEntryKind.BackgroundRun, entry.Kind);
        Assert.True(entry.IsQuiet);
        Assert.Null(test.Agent.LastAnalysis);
    }

    [Fact]
    public async Task WhenAWatchedPropertyChanges_ThenABackgroundRunIsTriggered()
    {
        // Arrange
        await using var test = await StartAsync(agent => agent.WatchPaths = ["/Temperature"]);

        // Act
        test.Root.Temperature = 30;
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.RunCount >= 1);

        // Assert
        Assert.Contains("Trigger: change", test.Agent.Requests[0].UserMessage);
    }

    [Fact]
    public async Task WhenAnUnwatchedPropertyChanges_ThenNoRunIsTriggeredButAWatchedChangeStillIs()
    {
        // Arrange
        await using var test = await StartAsync(agent => agent.WatchPaths = ["/Kitchen"]);

        // Act
        test.Root.Temperature = 30;
        test.Agent.RunNow();
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.RunCount == 1);

        // Assert
        Assert.Contains("Trigger: manual", Assert.Single(test.Agent.Requests).UserMessage);
    }

    [Fact]
    public async Task WhenAChatMessageFollowsAFinding_ThenTheFindingIsPrependedAndTheReplyRecorded()
    {
        // Arrange
        await using var test = await StartAsync(agent =>
        {
            agent.Model = "chat-model";
            agent.Handler = (request, _, _) => Task.FromResult(new AgentTurnResult(
                request.Kind == AgentTurnKind.Background ? "PV surplus 3.2 kW." : "Because the surplus is high.", null, null));
        });
        test.Agent.RunNow();
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.RunCount == 1);

        // Act
        await test.Agent.SendMessageAsync("Why should I charge?", CancellationToken.None);

        // Assert
        var chat = test.Agent.Requests[1];
        Assert.Equal(AgentTurnKind.Chat, chat.Kind);
        Assert.Equal("chat-model", chat.Model);
        Assert.Contains("PV surplus 3.2 kW.", chat.UserMessage);
        Assert.EndsWith("Why should I charge?", chat.UserMessage);
        Assert.Equal("PV surplus 3.2 kW.", test.Agent.LastAnalysis);
        Assert.Equal(
            [AgentTimelineEntryKind.BackgroundRun, AgentTimelineEntryKind.User, AgentTimelineEntryKind.Assistant],
            test.Agent.Timeline.Entries.Select(entry => entry.Kind));
    }

    [Fact]
    public async Task WhenATurnFailsWithAConfigurationError_ThenTheAgentIsUnavailableAndTheErrorIsRecorded()
    {
        // Arrange
        await using var test = await StartAsync(agent => agent.Handler = (_, _, _) =>
            throw new AgentTurnException(AgentFailureKind.Configuration, "Claude CLI not logged in"));

        // Act
        test.Agent.RunNow();
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.Status == ServiceStatus.Unavailable);

        // Assert
        Assert.Contains("not logged in", test.Agent.StatusMessage);
        Assert.Equal(AgentTimelineEntryKind.Error, Assert.Single(test.Agent.Timeline.Entries).Kind);
    }

    [Fact]
    public async Task WhenTheModelUpdatesMemory_ThenTheNextTurnSeesItAndItIsPersisted()
    {
        // Arrange
        await using var test = await StartAsync(agent => agent.Handler = async (request, _, cancellationToken) =>
        {
            var tool = request.Tools.Tools.First(tool => tool.Name == "update_memory");
            if (agent.Requests.Count == 1)
            {
                await tool.Handler(JsonSerializer.SerializeToElement(new { content = "- remember the freezer" }), cancellationToken);
            }

            return new AgentTurnResult("[quiet] ok", null, null);
        });

        // Act
        test.Agent.RunNow();
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.RunCount == 1);
        test.Agent.RunNow();
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.RunCount == 2);

        // Assert
        Assert.Contains("- remember the freezer", test.Agent.Requests[1].SystemPrompt);
        Assert.Equal("- remember the freezer", await File.ReadAllTextAsync(Path.Combine(test.DataDirectory, "Agents", "Agent.memory.md")));
    }

    [Fact]
    public async Task WhenANewConversationStarts_ThenTheSessionResetsAndADividerIsWritten()
    {
        // Arrange
        await using var test = await StartAsync();

        // Act
        await test.Agent.NewConversationAsync(CancellationToken.None);

        // Assert
        Assert.Equal(1, test.Agent.ResetCount);
        Assert.Equal(AgentTimelineEntryKind.Divider, Assert.Single(test.Agent.Timeline.Entries).Kind);
    }

    [Fact]
    public async Task WhenOnlyReadToolsAreAllowed_ThenTheTurnGetsThoseAndUpdateMemory()
    {
        // Arrange
        await using var test = await StartAsync(agent => agent.AllowedTools = ["browse"]);

        // Act
        test.Agent.RunNow();
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.RunCount == 1);

        // Assert
        Assert.Equal(["browse", "update_memory"], test.Agent.Requests[0].Tools.Tools.Select(tool => tool.Name));
    }

    [Fact]
    public async Task WhenTheIntervalIsReconfiguredBeyondTheMaximum_ThenTheAgentReportsAConfigurationErrorInsteadOfThrowing()
    {
        // Arrange
        await using var test = await StartAsync();
        test.Agent.PollInterval = TimeSpan.FromDays(400);

        // Act
        await test.Agent.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        Assert.Equal(ServiceStatus.Error, test.Agent.Status);
        Assert.Contains("Poll interval must not exceed", test.Agent.StatusMessage);
    }

    [Fact]
    public async Task WhenAnInvalidIntervalIsCorrected_ThenTheAgentIsIdleAgain()
    {
        // Arrange
        await using var test = await StartAsync();
        test.Agent.MinRunInterval = TimeSpan.FromDays(400);
        await test.Agent.ApplyConfigurationAsync(CancellationToken.None);

        // Act
        test.Agent.MinRunInterval = TimeSpan.FromMinutes(1);
        await test.Agent.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        Assert.Equal(ServiceStatus.Running, test.Agent.Status);
        Assert.Equal("Idle", test.Agent.StatusMessage);
    }

    [Fact]
    public async Task WhenTheAgentStartsWithAnIntervalBeyondTheMaximum_ThenItReportsTheErrorAndStillRunsManually()
    {
        // Arrange
        await using var test = await StartAsync(agent =>
        {
            agent.PollInterval = TimeSpan.FromDays(400);
            agent.WatchPaths = ["/Temperature"];
        });

        // Act
        test.Root.Temperature = 30;
        test.Agent.RunNow();
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.RunCount == 1 && !test.Agent.IsBusy);

        // Assert
        Assert.Contains("Trigger: manual", Assert.Single(test.Agent.Requests).UserMessage);
        Assert.Equal(ServiceStatus.Error, test.Agent.Status);
        Assert.Contains("Poll interval must not exceed", test.Agent.StatusMessage);
    }

    [Fact]
    public async Task WhenAChatTurnThrowsAnUnexpectedException_ThenTheFailureIsRecordedInsteadOfThrown()
    {
        // Arrange
        await using var test = await StartAsync(agent => agent.Handler = (_, _, _) =>
            throw new InvalidOperationException("backend broke"));

        // Act
        await test.Agent.SendMessageAsync("Hello", CancellationToken.None);

        // Assert
        Assert.Equal(
            [AgentTimelineEntryKind.User, AgentTimelineEntryKind.Error],
            test.Agent.Timeline.Entries.Select(entry => entry.Kind));
        Assert.Equal(ServiceStatus.Error, test.Agent.Status);
        Assert.False(test.Agent.IsBusy);
    }

    [Fact]
    public async Task WhenABackgroundTurnThrowsAnUnexpectedException_ThenLaterRunsStillHappen()
    {
        // Arrange
        await using var test = await StartAsync(agent => agent.Handler = (_, _, _) =>
            agent.Requests.Count == 1
                ? throw new InvalidOperationException("backend broke")
                : Task.FromResult(new AgentTurnResult("[quiet] ok", null, null)));

        // Act
        test.Agent.RunNow();
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.Timeline.Entries.Count == 1);
        test.Agent.RunNow();
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.RunCount == 1);

        // Assert
        Assert.Equal(
            [AgentTimelineEntryKind.Error, AgentTimelineEntryKind.BackgroundRun],
            test.Agent.Timeline.Entries.Select(entry => entry.Kind));
    }

    [Fact]
    public async Task WhenATimelineSubscriberThrows_ThenTheBackgroundLoopKeepsRunning()
    {
        // Arrange
        await using var test = await StartAsync();
        test.Agent.TimelineChanged += () => throw new InvalidOperationException("subscriber broke");

        // Act
        test.Agent.RunNow();
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.RunCount == 1);
        test.Agent.RunNow();
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.RunCount == 2);

        // Assert
        Assert.Equal(2, test.Agent.Timeline.Entries.Count);
    }
}
