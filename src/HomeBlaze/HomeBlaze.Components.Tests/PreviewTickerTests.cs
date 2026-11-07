using HomeBlaze.Components.Inputs;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class PreviewTickerTests
{
    [Fact]
    public void WhenOneSecondPasses_ThenTheTickRunsOnceThroughInvokeAsync()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var invokeCount = 0;
        var tickCount = 0;
        using var ticker = new PreviewTicker(
            timeProvider,
            callback =>
            {
                invokeCount++;
                return callback();
            },
            () =>
            {
                tickCount++;
                return Task.CompletedTask;
            });

        // Act
        timeProvider.Advance(TimeSpan.FromMilliseconds(999));
        var tickCountBeforeOneSecond = tickCount;
        timeProvider.Advance(TimeSpan.FromMilliseconds(1));

        // Assert
        Assert.Equal(0, tickCountBeforeOneSecond);
        Assert.Equal(1, invokeCount);
        Assert.Equal(1, tickCount);
    }

    [Fact]
    public async Task WhenDisposedWhileATickIsQueued_ThenTheTickIsNotCalled()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var queued = new List<Func<Task>>();
        var tickCount = 0;
        var ticker = new PreviewTicker(
            timeProvider,
            callback =>
            {
                queued.Add(callback);
                return Task.CompletedTask;
            },
            () =>
            {
                tickCount++;
                return Task.CompletedTask;
            });
        timeProvider.Advance(TimeSpan.FromSeconds(1));

        // Act
        ticker.Dispose();
        await Assert.Single(queued)();

        // Assert
        Assert.Equal(0, tickCount);
    }

    [Fact]
    public void WhenCreated_ThenItsTimerDoesNotCaptureTheExecutionContextOfTheCaller()
    {
        // Arrange
        var timeProvider = new FlowRecordingTimeProvider();

        // Act
        using var ticker = new PreviewTicker(timeProvider, callback => callback(), () => Task.CompletedTask);

        // Assert
        Assert.True(timeProvider.WasFlowSuppressed);
        Assert.False(ExecutionContext.IsFlowSuppressed());
    }

    private sealed class FlowRecordingTimeProvider : FakeTimeProvider
    {
        public bool WasFlowSuppressed { get; private set; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            WasFlowSuppressed = ExecutionContext.IsFlowSuppressed();
            return base.CreateTimer(callback, state, dueTime, period);
        }
    }
}
