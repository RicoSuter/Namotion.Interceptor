using HomeBlaze.Components.Inputs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class PreviewTickerTests
{
    [Fact]
    public void WhenOneSecondPasses_ThenEveryCallbackRunsOnceThroughOneDispatch()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var logger = new RecordingLogger();
        using var ticker = new PreviewTicker(timeProvider, logger);
        var invokeCount = 0;
        var firstTickCount = 0;
        var secondTickCount = 0;
        Task InvokeAsync(Func<Task> callback)
        {
            invokeCount++;
            return callback();
        }

        using var first = ticker.Subscribe(InvokeAsync, () =>
        {
            firstTickCount++;
            return Task.CompletedTask;
        });
        using var second = ticker.Subscribe(InvokeAsync, () =>
        {
            secondTickCount++;
            return Task.CompletedTask;
        });

        // Act
        timeProvider.Advance(TimeSpan.FromMilliseconds(999));
        var tickCountBeforeOneSecond = firstTickCount + secondTickCount;
        timeProvider.Advance(TimeSpan.FromMilliseconds(1));

        // Assert
        Assert.Equal(0, tickCountBeforeOneSecond);
        Assert.Equal(1, invokeCount);
        Assert.Equal(1, firstTickCount);
        Assert.Equal(1, secondTickCount);
        Assert.Empty(logger.Errors);
    }

    [Fact]
    public async Task WhenASubscriptionIsDisposedWhileATickIsQueued_ThenItsCallbackIsNotCalled()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        using var ticker = new PreviewTicker(timeProvider, new RecordingLogger());
        var queued = new List<Func<Task>>();
        var tickCount = 0;
        var subscription = ticker.Subscribe(
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
        subscription.Dispose();
        await Assert.Single(queued)();

        // Assert
        Assert.Equal(0, tickCount);
    }

    [Fact]
    public void WhenTheLastSubscriptionIsDisposed_ThenTheTimerStopsUntilTheNextSubscription()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        using var ticker = new PreviewTicker(timeProvider, new RecordingLogger());
        var tickCount = 0;
        Task Tick()
        {
            tickCount++;
            return Task.CompletedTask;
        }

        var subscription = ticker.Subscribe(callback => callback(), Tick);
        timeProvider.Advance(TimeSpan.FromSeconds(1));

        // Act
        subscription.Dispose();
        timeProvider.Advance(TimeSpan.FromSeconds(5));
        var tickCountWhileUnsubscribed = tickCount;
        using var next = ticker.Subscribe(callback => callback(), Tick);
        timeProvider.Advance(TimeSpan.FromSeconds(1));

        // Assert
        Assert.Equal(1, tickCountWhileUnsubscribed);
        Assert.Equal(2, tickCount);
    }

    [Fact]
    public void WhenTheTickerIsDisposed_ThenNoCallbackRunsAndALaterSubscriptionIsNotCalled()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var ticker = new PreviewTicker(timeProvider, new RecordingLogger());
        var tickCount = 0;
        Task Tick()
        {
            tickCount++;
            return Task.CompletedTask;
        }

        using var subscription = ticker.Subscribe(callback => callback(), Tick);

        // Act
        ticker.Dispose();
        using var late = ticker.Subscribe(callback => callback(), Tick);
        timeProvider.Advance(TimeSpan.FromSeconds(2));

        // Assert
        Assert.Equal(0, tickCount);
    }

    [Fact]
    public void WhenTheFirstSubscriptionIsMade_ThenTheTimerDoesNotCaptureTheExecutionContextOfTheCaller()
    {
        // Arrange
        var timeProvider = new FlowRecordingTimeProvider();
        using var ticker = new PreviewTicker(timeProvider, new RecordingLogger());

        // Act
        using var subscription = ticker.Subscribe(callback => callback(), () => Task.CompletedTask);

        // Assert
        Assert.True(timeProvider.WasFlowSuppressed);
        Assert.False(ExecutionContext.IsFlowSuppressed());
    }

    [Fact]
    public void WhenACallbackThrows_ThenTheErrorIsLoggedAndTheOtherCallbacksStillRun()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var logger = new RecordingLogger();
        using var ticker = new PreviewTicker(timeProvider, logger);
        var tickCount = 0;
        using var failing = ticker.Subscribe(callback => callback(), () => throw new InvalidOperationException("boom"));
        using var working = ticker.Subscribe(callback => callback(), () =>
        {
            tickCount++;
            return Task.CompletedTask;
        });

        // Act
        timeProvider.Advance(TimeSpan.FromSeconds(2));

        // Assert
        Assert.Equal(2, tickCount);
        Assert.Equal(2, logger.Errors.Count);
        Assert.All(logger.Errors, error => Assert.IsType<InvalidOperationException>(error));
    }

    [Fact]
    public void WhenTheDispatchFails_ThenTheErrorIsLoggedAndTheNextTickIsDispatched()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var logger = new RecordingLogger();
        using var ticker = new PreviewTicker(timeProvider, logger);
        var invokeCount = 0;
        using var subscription = ticker.Subscribe(
            _ =>
            {
                invokeCount++;
                return Task.FromException(new InvalidOperationException("the renderer is gone"));
            },
            () => Task.CompletedTask);

        // Act
        timeProvider.Advance(TimeSpan.FromSeconds(2));

        // Assert
        Assert.Equal(2, invokeCount);
        Assert.Equal(2, logger.Errors.Count);
    }

    [Fact]
    public void WhenATickStillRunsWhenTheNextIsDue_ThenTheNextIsSkipped()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        using var ticker = new PreviewTicker(timeProvider, new RecordingLogger());
        var dispatch = new TaskCompletionSource();
        var invokeCount = 0;
        using var subscription = ticker.Subscribe(
            _ =>
            {
                invokeCount++;
                return dispatch.Task;
            },
            () => Task.CompletedTask);

        // Act
        timeProvider.Advance(TimeSpan.FromSeconds(3));
        var invokeCountWhileRunning = invokeCount;
        dispatch.SetResult();
        timeProvider.Advance(TimeSpan.FromSeconds(1));

        // Assert
        Assert.Equal(1, invokeCountWhileRunning);
        Assert.Equal(2, invokeCount);
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

    private sealed class RecordingLogger : ILogger<PreviewTicker>
    {
        public List<Exception?> Errors { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error)
            {
                Errors.Add(exception);
            }
        }
    }
}
