using System.Collections.Concurrent;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using HomeBlaze.Abstractions;
using HomeBlaze.AI.Agents;
using Namotion.Interceptor;
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

    private static async Task<AgentTurnResult> WaitForCancellationAsync(
        AgentTurnRequest request, IProgress<AgentTurnUpdate> progress, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return new AgentTurnResult(string.Empty, null, null);
    }
}
