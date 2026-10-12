using System.Text.Json;
using HomeBlaze.Abstractions;
using HomeBlaze.AI.Agents;
using Microsoft.Extensions.Hosting;
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
        var logger = new RecordingLogger();
        await using var test = await AgentTestContext<FakeAgent>.StartAsync(
            services => new FakeAgent(services, logger),
            agent => agent.WatchPaths = ["/Humidity"]);

        // Act
        test.Root.Temperature = 30;
        test.Root.Humidity = 50;
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.RunCount == 1);

        // Assert
        // Changes are evaluated in order, so once the later watched change has caused a run, the earlier
        // unwatched change has been evaluated too.
        var runRequests = GetChangeRunRequests(logger);
        Assert.Contains("Humidity", Assert.Single(runRequests));
        Assert.Contains("Trigger: change", Assert.Single(test.Agent.Requests).UserMessage);
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
                await tool.Handler(JsonSerializer.SerializeToElement(new { changes = new[] { new { key = "alerts/freezer", value = "remember the freezer" } } }), cancellationToken);
            }

            return new AgentTurnResult("[quiet] ok", null, null);
        });

        // Act
        test.Agent.RunNow();
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.RunCount == 1);
        test.Agent.RunNow();
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.RunCount == 2);

        // Assert
        Assert.Contains("""{"key":"alerts/freezer","value":"remember the freezer",""", test.Agent.Requests[1].SystemPrompt);
        Assert.Contains("remember the freezer", await File.ReadAllTextAsync(Path.Combine(test.DataDirectory, "Agents", "Agent.memory.json")));
    }

    [Fact]
    public async Task WhenAnOldMarkdownMemoryExists_ThenTheAgentMigratesItAndShowsItInThePrompt()
    {
        // Arrange
        await using var test = await StartAsync();
        await ((IHostedService)test.Agent).StopAsync(CancellationToken.None);
        var legacyPath = Path.Combine(test.DataDirectory, "Agents", "Agent.memory.md");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        await File.WriteAllTextAsync(legacyPath, "- remember the freezer");

        // Act
        await RestartAsync(test.Agent);
        test.Agent.RunNow();
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.RunCount == 1);

        // Assert
        Assert.Contains("""{"key":"notes/legacy","value":"- remember the freezer",""", test.Agent.Requests[0].SystemPrompt);
        Assert.True(File.Exists(Path.Combine(test.DataDirectory, "Agents", "Agent.memory.json")));
        Assert.True(File.Exists(legacyPath + ".migrated"));
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
        var logger = new RecordingLogger();
        await using var test = await AgentTestContext<FakeAgent>.StartAsync(
            services => new FakeAgent(services, logger),
            agent =>
            {
                agent.PollInterval = TimeSpan.FromDays(400);
                agent.WatchPaths = ["/Temperature"];
            });

        // Act
        test.Root.Temperature = 30;
        await AsyncTestHelpers.WaitUntilAsync(() => logger.Entries.Any(entry => entry.Message.Contains("trigger configuration is invalid") && entry.Message.StartsWith("Ignored")));
        test.Agent.RunNow();
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.RunCount == 1 && !test.Agent.IsBusy);

        // Assert
        Assert.Empty(GetChangeRunRequests(logger));
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
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.RunCount == 2);

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

    [Fact]
    public async Task WhenTheCallerCancelsARunningChat_ThenTheStatusIsRestoredAndTheCancellationRecorded()
    {
        // Arrange
        await using var test = await StartAsync(agent => agent.Handler = WaitForCancellationAsync);
        using var cancellation = new CancellationTokenSource();
        var chat = test.Agent.SendMessageAsync("Hello", cancellation.Token);
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.IsBusy);

        // Act
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => chat);

        // Assert
        Assert.Equal(ServiceStatus.Running, test.Agent.Status);
        Assert.Equal("Idle", test.Agent.StatusMessage);
        Assert.False(test.Agent.IsBusy);
        Assert.Equal(
            [AgentTimelineEntryKind.User, AgentTimelineEntryKind.Error],
            test.Agent.Timeline.Entries.Select(entry => entry.Kind));
        Assert.Equal(LlmAgentBase.CancelledText, test.Agent.Timeline.Entries[1].Text);
    }

    [Fact]
    public async Task WhenTheCallerCancelsAChatWaitingForABackgroundRun_ThenTheWaitingMessageIsRemoved()
    {
        // Arrange
        var release = new TaskCompletionSource();
        await using var test = await StartAsync(agent => agent.Handler = async (request, _, cancellationToken) =>
        {
            if (request.Kind == AgentTurnKind.Background)
            {
                await release.Task.WaitAsync(cancellationToken);
            }

            return new AgentTurnResult("[quiet] ok", null, null);
        });
        test.Agent.RunNow();
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.IsBusy);
        using var cancellation = new CancellationTokenSource();
        var chat = test.Agent.SendMessageAsync("Hello", cancellation.Token);
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.StatusMessage == "Waiting for the background run");

        // Act
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => chat);

        // Assert
        Assert.Equal("Running", test.Agent.StatusMessage);
        Assert.Equal(
            [AgentTimelineEntryKind.User, AgentTimelineEntryKind.Error],
            test.Agent.Timeline.Entries.Select(entry => entry.Kind));
        release.SetResult();
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.RunCount == 1 && !test.Agent.IsBusy);
        Assert.Equal("Idle", test.Agent.StatusMessage);
        Assert.Single(test.Agent.Requests);
    }

    [Fact]
    public async Task WhenTheAgentStopsDuringAChat_ThenTheChatIsCancelledAndTheAgentStaysStopped()
    {
        // Arrange
        await using var test = await StartAsync(agent => agent.Handler = WaitForCancellationAsync);
        var chat = test.Agent.SendMessageAsync("Hello", CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.IsBusy);

        // Act
        await ((IHostedService)test.Agent).StopAsync(CancellationToken.None);
        await chat;

        // Assert
        Assert.Equal(ServiceStatus.Stopped, test.Agent.Status);
        Assert.False(test.Agent.IsBusy);
        Assert.Equal(
            [AgentTimelineEntryKind.User, AgentTimelineEntryKind.Error],
            test.Agent.Timeline.Entries.Select(entry => entry.Kind));
    }

    [Fact]
    public async Task WhenTheAgentIsStopped_ThenAChatIsRecordedAsNotRunningAndRunNowDoesNothing()
    {
        // Arrange
        await using var test = await StartAsync();
        await ((IHostedService)test.Agent).StopAsync(CancellationToken.None);

        // Act
        test.Agent.RunNow();
        await test.Agent.ApplyConfigurationAsync(CancellationToken.None);
        await test.Agent.SendMessageAsync("Hello", CancellationToken.None);

        // Assert
        Assert.Equal(ServiceStatus.Stopped, test.Agent.Status);
        Assert.Empty(test.Agent.Requests);
        Assert.Equal(
            [AgentTimelineEntryKind.User, AgentTimelineEntryKind.Error],
            test.Agent.Timeline.Entries.Select(entry => entry.Kind));
    }

    [Fact]
    public async Task WhenATurnExceedsTheTurnTimeout_ThenTheRunFailsWithATimeoutError()
    {
        // Arrange
        await using var test = await StartAsync(agent =>
        {
            agent.TurnTimeout = TimeSpan.FromMilliseconds(100);
            agent.Handler = WaitForCancellationAsync;
        });

        // Act
        test.Agent.RunNow();
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.Status == ServiceStatus.Error);

        // Assert
        Assert.Contains("did not finish", test.Agent.StatusMessage);
        var entry = Assert.Single(test.Agent.Timeline.Entries);
        Assert.Equal(AgentTimelineEntryKind.Error, entry.Kind);
        Assert.Contains("did not finish", entry.Text);
    }

    [Fact]
    public async Task WhenATurnStartsANewConversationOfItsOwnAgent_ThenItFailsFastInsteadOfDeadlocking()
    {
        // Arrange
        await using var test = await StartAsync(agent => agent.Handler = async (_, _, cancellationToken) =>
        {
            try
            {
                await agent.NewConversationAsync(cancellationToken);
                return new AgentTurnResult("reset", null, null);
            }
            catch (InvalidOperationException exception)
            {
                return new AgentTurnResult(exception.Message, null, null);
            }
        });

        // Act
        test.Agent.RunNow();
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.RunCount == 1);

        // Assert
        Assert.Contains("within a turn", Assert.Single(test.Agent.Timeline.Entries).Text);
        Assert.Equal(0, test.Agent.ResetCount);
    }

    [Fact]
    public async Task WhenAStaleRunEndsAfterARestart_ThenTheNewRunKeepsRunning()
    {
        // Arrange
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var test = await StartAsync(agent => agent.Handler = async (_, _, _) =>
        {
            if (agent.Requests.Count == 1)
            {
                // Ignores cancellation, so the first run outlives its stop.
                await release.Task;
            }

            return new AgentTurnResult("[quiet] ok", null, null);
        });
        test.Agent.RunNow();
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.IsBusy);
        var staleRun = test.Agent.ExecuteTask!;
        using var expired = new CancellationTokenSource();
        await expired.CancelAsync();
        try
        {
            await ((IHostedService)test.Agent).StopAsync(expired.Token);
        }
        catch (OperationCanceledException)
        {
            // The stop gave up waiting for the run, which is the situation under test.
        }

        // Act
        await ((IHostedService)test.Agent).StartAsync(CancellationToken.None);
        release.SetResult();
        await staleRun;

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() =>
        {
            test.Agent.RunNow();
            return test.Agent.RunCount == 2 && !test.Agent.IsBusy;
        });
        Assert.Equal(ServiceStatus.Running, test.Agent.Status);
        Assert.Equal("Idle", test.Agent.StatusMessage);
    }

    [Fact]
    public async Task WhenABackgroundRunFails_ThenItIsCountedWithItsTriggerAndSetsTheLastRun()
    {
        // Arrange
        await using var test = await StartAsync(agent => agent.Handler = (_, _, _) =>
            throw new AgentTurnException(AgentFailureKind.Transient, "backend broke"));

        // Act
        test.Agent.RunNow();
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.Status == ServiceStatus.Error);

        // Assert
        var entry = Assert.Single(test.Agent.Timeline.Entries);
        Assert.Equal(AgentTimelineEntryKind.Error, entry.Kind);
        Assert.Equal("manual", entry.Trigger);
        Assert.Equal(1, test.Agent.RunCount);
        Assert.Equal(entry.Timestamp, test.Agent.LastRunTime);
    }

    [Fact]
    public async Task WhenTheAgentStopsDuringABackgroundRun_ThenTheCancelledRunIsCountedAndRecorded()
    {
        // Arrange
        await using var test = await StartAsync(agent => agent.Handler = WaitForCancellationAsync);
        test.Agent.RunNow();
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.IsBusy);

        // Act
        await ((IHostedService)test.Agent).StopAsync(CancellationToken.None);

        // Assert
        var entry = Assert.Single(test.Agent.Timeline.Entries);
        Assert.Equal(AgentTimelineEntryKind.Error, entry.Kind);
        Assert.Equal("manual", entry.Trigger);
        Assert.Equal(1, test.Agent.RunCount);
        Assert.NotNull(test.Agent.LastRunTime);
    }

    [Fact]
    public async Task WhenAChatTurnRuns_ThenItIsNotCountedAsABackgroundRun()
    {
        // Arrange
        await using var test = await StartAsync();

        // Act
        await test.Agent.SendMessageAsync("Hello", CancellationToken.None);

        // Assert
        Assert.Equal(0, test.Agent.RunCount);
        Assert.Null(test.Agent.LastRunTime);
    }

    [Fact]
    public async Task WhenAChatTurnRuns_ThenTheLastActivityIsSetButNotTheLastRun()
    {
        // Arrange
        await using var test = await StartAsync();

        // Act
        await test.Agent.SendMessageAsync("Hello", CancellationToken.None);

        // Assert
        var userEntry = test.Agent.Timeline.Entries[0];
        Assert.NotNull(test.Agent.LastActivityTime);
        Assert.True(test.Agent.LastActivityTime >= userEntry.Timestamp);
        Assert.True(test.Agent.LastActivityTime <= test.Agent.Timeline.Entries[^1].Timestamp);
        Assert.Null(test.Agent.LastRunTime);
        Assert.Equal(0, test.Agent.RunCount);
    }

    [Fact]
    public async Task WhenAChatTurnFails_ThenTheLastActivityIsSet()
    {
        // Arrange
        await using var test = await StartAsync(agent => agent.Handler = (_, _, _) =>
            throw new AgentTurnException(AgentFailureKind.Transient, "backend broke"));

        // Act
        await test.Agent.SendMessageAsync("Hello", CancellationToken.None);

        // Assert
        Assert.Equal(AgentTimelineEntryKind.Error, test.Agent.Timeline.Entries[^1].Kind);
        Assert.NotNull(test.Agent.LastActivityTime);
        Assert.Null(test.Agent.LastRunTime);
    }

    [Fact]
    public async Task WhenAChatMessageIsNotAnswered_ThenTheLastActivityIsNotSet()
    {
        // Arrange
        await using var test = await StartAsync();
        await ((IHostedService)test.Agent).StopAsync(CancellationToken.None);

        // Act
        await test.Agent.SendMessageAsync("Hello", CancellationToken.None);

        // Assert
        Assert.Equal("The agent is not running.", test.Agent.Timeline.Entries[^1].Text);
        Assert.Null(test.Agent.LastActivityTime);
    }

    [Fact]
    public async Task WhenABackgroundRunRuns_ThenTheLastActivityAndTheLastRunAreItsStart()
    {
        // Arrange
        await using var test = await StartAsync();

        // Act
        test.Agent.RunNow();
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.RunCount == 1 && !test.Agent.IsBusy);

        // Assert
        Assert.NotNull(test.Agent.LastRunTime);
        Assert.Equal(test.Agent.LastRunTime, test.Agent.LastActivityTime);
        Assert.Equal(test.Agent.Timeline.Entries[^1].Timestamp, test.Agent.LastActivityTime);
    }

    [Fact]
    public async Task WhenTheAgentRestarts_ThenTheLastActivityComesFromTheStatsFile()
    {
        // Arrange
        await using var test = await StartAsync();
        await test.Agent.SendMessageAsync("Hello", CancellationToken.None);
        var lastActivity = test.Agent.LastActivityTime;
        await ((IHostedService)test.Agent).StopAsync(CancellationToken.None);
        var statsPath = Path.Combine(test.DataDirectory, "Agents", "Agent.stats.json");
        Assert.True(JsonDocument.Parse(await File.ReadAllTextAsync(statsPath)).RootElement.TryGetProperty("lastActivityTime", out _));

        // Act
        await RestartAsync(test.Agent);

        // Assert
        Assert.NotNull(lastActivity);
        Assert.Equal(lastActivity, test.Agent.LastActivityTime);
        Assert.Null(test.Agent.LastRunTime);
    }

    [Fact]
    public async Task WhenTheStatsFileHasNoLastActivity_ThenItIsTakenFromTheNewestTimelineEntry()
    {
        // Arrange
        await using var test = await StartAsync();
        await test.Agent.SendMessageAsync("Hello", CancellationToken.None);
        await ((IHostedService)test.Agent).StopAsync(CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(test.DataDirectory, "Agents", "Agent.stats.json"), """{"backgroundRunCount":0}""");

        // Act
        await RestartAsync(test.Agent);

        // Assert
        Assert.Equal(test.Agent.Timeline.Entries[^1].Timestamp, test.Agent.LastActivityTime);
    }

    [Fact]
    public async Task WhenANewConversationStarts_ThenTheLastActivityIsKept()
    {
        // Arrange
        await using var test = await StartAsync();
        await test.Agent.SendMessageAsync("Hello", CancellationToken.None);
        var lastActivity = test.Agent.LastActivityTime;

        // Act
        await test.Agent.NewConversationAsync(CancellationToken.None);
        await RestartAsync(test.Agent);

        // Assert
        Assert.NotNull(lastActivity);
        Assert.Equal(lastActivity, test.Agent.LastActivityTime);
    }

    [Fact]
    public async Task WhenTheAgentRestarts_ThenTheRunCountComesFromTheStatsFileAndTheLastRunFromTheTimeline()
    {
        // Arrange
        await using var test = await StartAsync(agent => agent.Handler = (_, _, _) =>
            agent.Requests.Count == 1
                ? Task.FromResult(new AgentTurnResult("[quiet] ok", null, null))
                : throw new AgentTurnException(AgentFailureKind.Transient, "backend broke"));
        test.Agent.RunNow();
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.RunCount == 1 && !test.Agent.IsBusy);
        test.Agent.RunNow();
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.RunCount == 2 && !test.Agent.IsBusy);
        var lastRun = test.Agent.LastRunTime;
        await ((IHostedService)test.Agent).StopAsync(CancellationToken.None);
        var statsPath = Path.Combine(test.DataDirectory, "Agents", "Agent.stats.json");
        Assert.Equal(2, JsonDocument.Parse(await File.ReadAllTextAsync(statsPath)).RootElement.GetProperty("backgroundRunCount").GetInt32());
        await File.WriteAllTextAsync(statsPath, """{"backgroundRunCount":7}""");

        // Act
        await RestartAsync(test.Agent);

        // Assert
        Assert.Equal(7, test.Agent.RunCount);
        Assert.Equal(lastRun, test.Agent.LastRunTime);
        Assert.Equal(test.Agent.Timeline.Entries[^1].Timestamp, test.Agent.LastRunTime);
    }

    [Fact]
    public async Task WhenTheAgentRestartsWithoutAStatsFile_ThenTheRunCountIsTakenFromTheTimeline()
    {
        // Arrange
        await using var test = await StartAsync();
        test.Agent.RunNow();
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.RunCount == 1 && !test.Agent.IsBusy);
        await test.Agent.SendMessageAsync("Hello", CancellationToken.None);
        var statsPath = Path.Combine(test.DataDirectory, "Agents", "Agent.stats.json");
        await ((IHostedService)test.Agent).StopAsync(CancellationToken.None);
        await File.WriteAllTextAsync(statsPath, """{"backgroundRunCount":7}""");
        // A restart loads the count 7, so the count after the next restart can only come from the timeline.
        await RestartAsync(test.Agent);
        await ((IHostedService)test.Agent).StopAsync(CancellationToken.None);
        File.Delete(statsPath);

        // Act
        await RestartAsync(test.Agent);

        // Assert
        Assert.Equal(1, test.Agent.RunCount);
    }

    [Fact]
    public async Task WhenANewConversationStarts_ThenTheRunCountAndLastRunAreKept()
    {
        // Arrange
        await using var test = await StartAsync();
        test.Agent.RunNow();
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.RunCount == 1 && !test.Agent.IsBusy);
        var lastRun = test.Agent.LastRunTime;

        // Act
        await test.Agent.NewConversationAsync(CancellationToken.None);
        await RestartAsync(test.Agent);

        // Assert
        Assert.Equal(1, test.Agent.RunCount);
        Assert.Equal(lastRun, test.Agent.LastRunTime);
    }

    private static async Task RestartAsync(FakeAgent agent)
    {
        await ((IHostedService)agent).StopAsync(CancellationToken.None);
        await ((IHostedService)agent).StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => agent.Status is not (ServiceStatus.Stopped or ServiceStatus.Starting));
    }

    private static async Task<AgentTurnResult> WaitForCancellationAsync(
        AgentTurnRequest request, IProgress<AgentTurnUpdate> progress, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return new AgentTurnResult(string.Empty, null, null);
    }

    private static List<string> GetChangeRunRequests(RecordingLogger logger) =>
        logger.Entries
            .Where(entry => entry.Message.Contains("requests a background run"))
            .Select(entry => entry.Message)
            .ToList();
}
