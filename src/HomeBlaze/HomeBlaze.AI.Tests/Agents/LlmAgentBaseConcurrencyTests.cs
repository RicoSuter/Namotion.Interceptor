using System.Collections.Concurrent;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Text.Json;
using HomeBlaze.Abstractions;
using HomeBlaze.AI.Agents;
using Namotion.Interceptor;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace HomeBlaze.AI.Tests.Agents;

/// <summary>Turns, configuration changes, stops, restarts and status reports of an agent that overlap.</summary>
public class LlmAgentBaseConcurrencyTests
{
    private static Task<AgentTestContext<FakeAgent>> StartAsync(Action<FakeAgent>? configure = null) =>
        AgentTestContext<FakeAgent>.StartAsync(services => new FakeAgent(services), configure);

    [Fact]
    public async Task WhenTheConfigurationChangesDuringATurnThatThenFailsWithAConfigurationError_ThenAutomaticRunsOnlyBackOff()
    {
        // Arrange
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var test = await AgentTestContext<FakeAgent>.StartAsync(
            services => new FakeAgent(services),
            agent =>
            {
                agent.WatchPaths = ["/Temperature"];
                agent.Handler = async (_, _, _) =>
                {
                    if (agent.Requests.Count == 1)
                    {
                        // The request of the old configuration fails after the new one was saved.
                        await release.Task;
                        throw new AgentTurnException(AgentFailureKind.Configuration, "Invalid API key");
                    }

                    return new AgentTurnResult("[quiet] ok", null, null);
                };
            },
            timeProvider: time);
        test.Agent.RunNow();
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.Requests.Count == 1);

        // Act
        await test.Agent.ApplyConfigurationAsync(CancellationToken.None);
        release.SetResult();
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.Timeline.Entries.Count == 1 && !test.Agent.IsBusy);
        var statusAfterTheFailure = test.Agent.Status;
        time.Advance(TimeSpan.FromMinutes(1));
        test.Root.Temperature = 30;

        // Assert
        Assert.Equal(ServiceStatus.Error, statusAfterTheFailure);
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.Requests.Count == 2, message: "The watched change should start a run after the backoff.");
    }

    [Fact]
    public async Task WhenTheConfigurationChangesWhileAConfigurationFailureIsRecorded_ThenTheAgentEndsIdle()
    {
        // Arrange
        var isReconfigured = 0;
        await using var test = await StartAsync(agent => agent.Handler = (_, _, _) =>
            throw new AgentTurnException(AgentFailureKind.Configuration, "Invalid API key"));
        test.Agent.StateChanged += () =>
        {
            // Raised once the error entry is recorded, after the failure paused the agent and before its status is
            // written; a change saved there lifts the pause again.
            if (test.Agent.Timeline.Entries.Count == 1 && Interlocked.Exchange(ref isReconfigured, 1) == 0)
            {
                _ = test.Agent.ApplyConfigurationAsync(CancellationToken.None);
            }
        };

        // Act
        test.Agent.RunNow();
        await AsyncTestHelpers.WaitUntilAsync(() =>
            test.Agent is { IsBusy: false, StatusMessage: not LlmAgentBase.BackgroundRunStatusMessage } && test.Agent.Timeline.Entries.Count == 1);

        // Assert
        Assert.Equal(1, isReconfigured);
        Assert.Equal(ServiceStatus.Running, test.Agent.Status);
        Assert.Equal("Idle", test.Agent.StatusMessage);
    }

    [Fact]
    public async Task WhenAnUnrelatedChangeIsSavedWhileTheFailureOfATurnThatSawTheIntervalsFixedIsRecorded_ThenTheStatusShowsTheFailure()
    {
        // Arrange
        var isReconfigured = 0;
        await using var test = await StartAsync();
        test.Agent.PollInterval = TimeSpan.FromDays(400);
        await test.Agent.ApplyConfigurationAsync(CancellationToken.None);
        test.Agent.Handler = async (_, _, _) =>
        {
            // The fix is saved while the turn runs, so the turn is left to report it.
            test.Agent.PollInterval = null;
            await test.Agent.ApplyConfigurationAsync(CancellationToken.None);
            throw new AgentTurnException(AgentFailureKind.Transient, "backend broke");
        };
        test.Agent.StateChanged += () =>
        {
            // Raised once the error entry is recorded, after the failure was reported and before its status is
            // written; this change neither breaks nor fixes anything.
            if (test.Agent.Timeline.Entries.Count == 2 && Interlocked.Exchange(ref isReconfigured, 1) == 0)
            {
                _ = test.Agent.ApplyConfigurationAsync(CancellationToken.None);
            }
        };

        // Act
        await test.Agent.SendMessageAsync("Hello", CancellationToken.None);

        // Assert
        Assert.Equal(1, isReconfigured);
        Assert.Equal(ServiceStatus.Error, test.Agent.Status);
        Assert.Equal("backend broke", test.Agent.StatusMessage);
    }

    [Fact]
    public async Task WhenTheAgentStopsDuringAChat_ThenItReportsNoIdleStatusBeforeItIsStopped()
    {
        // Arrange
        await using var test = await StartAsync(agent => agent.Handler = WaitForCancellationAsync);
        var chat = test.Agent.SendMessageAsync("Hello", CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.IsBusy);
        var statusMessages = new ConcurrentQueue<string?>();
        using var subscription = ((IInterceptorSubject)test.Agent).Context.GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Where(change => ReferenceEquals(change.Property.Subject, test.Agent) &&
                             change.Property.Name == nameof(LlmAgentBase.StatusMessage))
            .Subscribe(change => statusMessages.Enqueue(change.GetNewValue<string?>()));

        // Act
        await test.StopAgentAsync();
        await chat;

        // Assert
        Assert.DoesNotContain("Idle", statusMessages);
        Assert.Equal(ServiceStatus.Stopped, test.Agent.Status);
    }

    [Fact]
    public async Task WhenAChatFailsWhileTheIntervalsAreInvalid_ThenTheStatusStillShowsTheConfigurationError()
    {
        // Arrange
        await using var test = await StartAsync(agent => agent.Handler = (_, _, _) =>
            throw new AgentTurnException(AgentFailureKind.Transient, "backend broke"));
        test.Agent.PollInterval = TimeSpan.FromDays(400);
        await test.Agent.ApplyConfigurationAsync(CancellationToken.None);

        // Act
        await test.Agent.SendMessageAsync("Hello", CancellationToken.None);

        // Assert
        Assert.Equal(ServiceStatus.Error, test.Agent.Status);
        Assert.Contains("Poll interval must not exceed", test.Agent.StatusMessage);
        Assert.Equal("backend broke", test.Agent.Timeline.Entries[^1].Text);
    }

    [Fact]
    public async Task WhenAUsageLimitPauseEndsAfterALaterRunFailed_ThenTheStatusKeepsTheFailure()
    {
        // Arrange
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
        await using var test = await AgentTestContext<FakeAgent>.StartAsync(
            services => new FakeAgent(services),
            agent =>
            {
                agent.WatchPaths = ["/Temperature"];
                agent.Handler = (_, _, _) => agent.Requests.Count == 1
                    ? throw new AgentTurnException(AgentFailureKind.UsageLimit, "Usage limit reached", retryAfter: time.GetUtcNow().AddMinutes(1))
                    : throw new AgentTurnException(AgentFailureKind.Transient, "backend broke");
            },
            timeProvider: time);
        await test.RunNowAndWaitAsync();
        await test.RunNowAndWaitAsync();
        await AsyncTestHelpers.WaitUntilAsync(() => time.PendingTimerCount == 1);

        // Act
        time.Advance(TimeSpan.FromMinutes(1));

        // Assert - the change waits for the backoff of two minutes on a timer that the scheduler creates only after it
        // handled the end of the pause.
        await AsyncTestHelpers.WaitUntilAsync(() => time.PendingTimerCount == 0);
        test.Root.Temperature = 30;
        await AsyncTestHelpers.WaitUntilAsync(() => time.PendingTimerCount == 1);
        Assert.Equal(ServiceStatus.Error, test.Agent.Status);
        Assert.Equal("backend broke", test.Agent.StatusMessage);
    }

    [Fact]
    public async Task WhenAUsageLimitOfAChatEndsBeforeItsFailureIsRecorded_ThenTheAgentEndsIdle()
    {
        // Arrange
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
        await using var test = await AgentTestContext<FakeAgent>.StartAsync(
            services => new FakeAgent(services),
            agent =>
            {
                // The loop then waits on a poll timer, and creates a new one once it handled the end of the pause.
                agent.PollInterval = TimeSpan.FromHours(1);
                agent.TurnTimeout = TimeSpan.Zero;
                agent.Handler = (_, _, _) => throw new AgentTurnException(
                    AgentFailureKind.UsageLimit, "Usage limit reached", retryAfter: time.GetUtcNow());
            },
            timeProvider: time);
        await AsyncTestHelpers.WaitUntilAsync(() => time.PendingTimerCount == 1);
        var createdTimerCount = time.CreatedTimerCount;
        var hasLoopHandledThePause = false;
        test.Agent.StateChanged += () =>
        {
            // Raised once the error entry is recorded, after the failure was reported and before its status is
            // written; the loop ends the pause meanwhile and finds the agent busy.
            if (test.Agent.Timeline.Entries.Count == 2 && !hasLoopHandledThePause)
            {
                hasLoopHandledThePause = SpinWait.SpinUntil(() => time.CreatedTimerCount > createdTimerCount, TimeSpan.FromSeconds(30));
            }
        };

        // Act
        await test.Agent.SendMessageAsync("Hello", CancellationToken.None);

        // Assert
        Assert.True(hasLoopHandledThePause);
        Assert.Equal(ServiceStatus.Running, test.Agent.Status);
        Assert.Equal("Idle", test.Agent.StatusMessage);
    }

    [Fact]
    public async Task WhenAnAgentWithoutStorageRestarts_ThenItKeepsItsTimelineAndMemoryInstances()
    {
        // Arrange
        await using var test = await AgentTestContext<FakeAgent>.StartAsync(services => new FakeAgent(services), hasDataDirectory: false);
        await test.Agent.SendMessageAsync("Hello", CancellationToken.None);
        var timeline = test.Agent.Timeline;
        var memory = test.Agent.Memory;

        // Act
        await test.RestartAgentAsync();

        // Assert - an append of a run that outlives its stop lands in the instance that the restarted agent shows.
        Assert.Same(timeline, test.Agent.Timeline);
        Assert.Same(memory, test.Agent.Memory);
        Assert.Equal(
            [AgentTimelineEntryKind.User, AgentTimelineEntryKind.Assistant],
            test.Agent.Timeline.Entries.Select(entry => entry.Kind));
    }

    [Fact]
    public async Task WhenTheAgentRestarts_ThenItKeepsItsTimelineInstanceWithItsEntries()
    {
        // Arrange
        await using var test = await StartAsync();
        await test.Agent.SendMessageAsync("Hello", CancellationToken.None);
        var timeline = test.Agent.Timeline;

        // Act
        await test.RestartAgentAsync();

        // Assert - an append of the stopped run and the load of the restart share the instance and its file lock.
        Assert.Same(timeline, test.Agent.Timeline);
        Assert.Equal(
            [AgentTimelineEntryKind.User, AgentTimelineEntryKind.Assistant],
            test.Agent.Timeline.Entries.Select(entry => entry.Kind));
    }

    [Fact]
    public async Task WhenAToolCallEndsAfterItsTurnReturned_ThenTheEntryOfTheTurnRecordsIt()
    {
        // Arrange
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
        var toolSource = new BlockingToolSource();
        await using var test = await StartWithRunningToolCallAsync(toolSource, time, logger: null);
        var chat = test.Agent.SendMessageAsync("Hello", CancellationToken.None);

        // The turn waits for the running call on the timer of its bounded wait.
        await AsyncTestHelpers.WaitUntilAsync(() => time.PendingTimerCount == 1);

        // Act
        toolSource.Release.SetResult();
        await chat;

        // Assert
        Assert.Equal("browse", Assert.Single(test.Agent.Timeline.Entries[^1].ToolCalls).Name);
    }

    [Fact]
    public async Task WhenAToolCallOutlastsTheWaitAfterItsTurn_ThenItIsLoggedAsMissingFromTheTimeline()
    {
        // Arrange
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
        var toolSource = new BlockingToolSource();
        var logger = new RecordingLogger();
        await using var test = await StartWithRunningToolCallAsync(toolSource, time, logger);
        var chat = test.Agent.SendMessageAsync("Hello", CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => time.PendingTimerCount == 1);

        // Act
        time.Advance(TimeSpan.FromSeconds(5));
        await chat;
        toolSource.Release.SetResult();

        // Assert
        Assert.Empty(test.Agent.Timeline.Entries[^1].ToolCalls);
        await AsyncTestHelpers.WaitUntilAsync(
            () => logger.Warnings.Any(warning => warning.Contains("browse", StringComparison.Ordinal)),
            message: "The late call should be logged with its tool name.");
    }

    /// <summary>Starts an agent whose turn leaves a call of <paramref name="toolSource"/> running when it returns.</summary>
    private static Task<AgentTestContext<FakeAgent>> StartWithRunningToolCallAsync(
        BlockingToolSource toolSource, TimeProvider time, RecordingLogger? logger) =>
        AgentTestContext<FakeAgent>.StartAsync(
            services => new FakeAgent(
                new AgentServices(toolSource, services.TokenRegistry, services.EndpointAddress, services.TimeProvider), logger),
            agent =>
            {
                agent.AllowedTools = ["browse"];
                agent.TurnTimeout = TimeSpan.Zero;
                agent.Handler = async (request, _, _) =>
                {
                    // The backend no longer waits for the call, as a timed out CLI turn does for a call over the MCP route.
                    _ = request.Tools.Tools.First(tool => tool.Name == "browse")
                        .Handler(JsonSerializer.SerializeToElement(new { }), CancellationToken.None);
                    await toolSource.Started.Task;
                    return new AgentTurnResult("Done.", null, null);
                };
            },
            timeProvider: time);

    private static async Task<AgentTurnResult> WaitForCancellationAsync(
        AgentTurnRequest request, IProgress<AgentTurnUpdate> progress, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return new AgentTurnResult(string.Empty, null, null);
    }
}

/// <summary>Offers a browse tool whose calls run until the test releases them.</summary>
internal sealed class BlockingToolSource : IAgentToolSource
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<McpToolInfo> CreateTools(bool isReadOnly) =>
    [
        new McpToolInfo
        {
            Name = "browse",
            Description = "browse",
            InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
            Handler = async (_, _) =>
            {
                Started.TrySetResult();
                await Release.Task;
                return "done";
            }
        }
    ];
}
