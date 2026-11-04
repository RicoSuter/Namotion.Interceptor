using System.Collections.Concurrent;
using HomeBlaze.AI.Agents;
using Namotion.Interceptor.Testing;
using Xunit;

namespace HomeBlaze.AI.Tests.Agents;

public class AgentTriggerSchedulerTests
{
    [Fact]
    public async Task WhenARunIsRequested_ThenTheCallbackRunsWithTheReason()
    {
        // Arrange
        var reasons = new ConcurrentQueue<string>();
        var scheduler = new AgentTriggerScheduler(TimeProvider.System, pollInterval: null, TimeSpan.Zero,
            (reason, _) => { reasons.Enqueue(reason); return Task.CompletedTask; });
        using var cancellation = new CancellationTokenSource();
        var loop = scheduler.RunAsync(cancellation.Token);

        // Act
        scheduler.RequestRun("change");
        await AsyncTestHelpers.WaitUntilAsync(() => reasons.Count == 1);
        await cancellation.CancelAsync();

        // Assert
        Assert.Equal("change", Assert.Single(reasons));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop);
    }

    [Fact]
    public async Task WhenAPollIntervalIsConfigured_ThenRunsRepeat()
    {
        // Arrange
        var count = 0;
        var scheduler = new AgentTriggerScheduler(TimeProvider.System, TimeSpan.FromMilliseconds(20), TimeSpan.Zero,
            (_, _) => { Interlocked.Increment(ref count); return Task.CompletedTask; });
        using var cancellation = new CancellationTokenSource();
        var loop = scheduler.RunAsync(cancellation.Token);

        // Act
        await AsyncTestHelpers.WaitUntilAsync(() => Volatile.Read(ref count) >= 3);
        await cancellation.CancelAsync();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop);
    }

    [Fact]
    public async Task WhenARunIsRequestedWhileOneIsRunning_ThenExactlyOneMoreRunFollows()
    {
        // Arrange
        var count = 0;
        var release = new TaskCompletionSource();
        var scheduler = new AgentTriggerScheduler(TimeProvider.System, pollInterval: null, TimeSpan.Zero,
            async (_, _) =>
            {
                if (Interlocked.Increment(ref count) == 1)
                {
                    await release.Task;
                }
            });
        using var cancellation = new CancellationTokenSource();
        var loop = scheduler.RunAsync(cancellation.Token);
        scheduler.RequestRun("change");
        await AsyncTestHelpers.WaitUntilAsync(() => Volatile.Read(ref count) == 1);

        // Act
        scheduler.RequestRun("change");
        scheduler.RequestRun("change");
        release.SetResult();
        await AsyncTestHelpers.WaitUntilAsync(() => Volatile.Read(ref count) == 2);
        scheduler.RequestManualRun();
        await AsyncTestHelpers.WaitUntilAsync(() => Volatile.Read(ref count) == 3);
        await cancellation.CancelAsync();

        // Assert
        Assert.Equal(3, Volatile.Read(ref count));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop);
    }

    [Fact]
    public async Task WhenThePollIntervalExceedsTheTimerRange_ThenTheLoopKeepsServingRequests()
    {
        // Arrange
        var count = 0;
        var scheduler = new AgentTriggerScheduler(TimeProvider.System, TimeSpan.FromDays(100), TimeSpan.Zero,
            (_, _) => { Interlocked.Increment(ref count); return Task.CompletedTask; });
        using var cancellation = new CancellationTokenSource();
        var loop = scheduler.RunAsync(cancellation.Token);
        scheduler.RequestRun("change");
        await AsyncTestHelpers.WaitUntilAsync(() => Volatile.Read(ref count) == 1);

        // Act
        scheduler.RequestRun("change");
        await AsyncTestHelpers.WaitUntilAsync(() => Volatile.Read(ref count) == 2);
        await cancellation.CancelAsync();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop);
    }

    [Fact]
    public async Task WhenASuccessOutsideARunLiftsAPause_ThenThePendingRunStarts()
    {
        // Arrange
        var count = 0;
        AgentTriggerScheduler? scheduler = null;
        scheduler = new AgentTriggerScheduler(TimeProvider.System, pollInterval: null, TimeSpan.Zero,
            (_, _) =>
            {
                if (Volatile.Read(ref count) == 0)
                {
                    scheduler!.RequestRun("change");
                    scheduler.ReportFailure(new AgentTurnException(AgentFailureKind.Configuration, "Not signed in."));
                }

                Interlocked.Increment(ref count);
                return Task.CompletedTask;
            });
        using var cancellation = new CancellationTokenSource();
        var loop = scheduler.RunAsync(cancellation.Token);
        scheduler.RequestRun("change");
        await AsyncTestHelpers.WaitUntilAsync(() => Volatile.Read(ref count) == 1);
        Assert.Equal("Not signed in.", scheduler.PauseReason);

        // Act
        scheduler.ReportSuccess();
        await AsyncTestHelpers.WaitUntilAsync(() => Volatile.Read(ref count) == 2);
        await cancellation.CancelAsync();

        // Assert
        Assert.Null(scheduler.PauseReason);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop);
    }

    [Fact]
    public async Task WhenAUsageLimitPauseEndsWithoutAPendingRun_ThenThePauseIsLiftedAndReported()
    {
        // Arrange
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
        var liftedCount = 0;
        var scheduler = new AgentTriggerScheduler(time, pollInterval: null, TimeSpan.Zero,
            (_, _) => Task.CompletedTask,
            () => Interlocked.Increment(ref liftedCount));
        using var cancellation = new CancellationTokenSource();
        var loop = scheduler.RunAsync(cancellation.Token);
        scheduler.ReportFailure(new AgentTurnException(
            AgentFailureKind.UsageLimit, "Usage limit reached", retryAfter: time.GetUtcNow().AddMinutes(10)));
        await AsyncTestHelpers.WaitUntilAsync(() => time.PendingTimerCount == 1);

        // Act
        time.Advance(TimeSpan.FromMinutes(10));
        await AsyncTestHelpers.WaitUntilAsync(() => Volatile.Read(ref liftedCount) == 1);
        await cancellation.CancelAsync();

        // Assert
        Assert.Null(scheduler.PauseReason);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop);
    }
}
