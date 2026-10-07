using System.Collections.Concurrent;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;

namespace HomeBlaze.Host.Services.Tests;

public class PropertyChangeWatcherTests
{
    [Fact]
    public async Task WhenWatchedPropertyIsWritten_ThenCallbackIsDispatchedOutsideTheWrite()
    {
        // Arrange
        var (subject, dispatcher) = CreateSubjectAndDispatcher();
        var callbackCount = 0;
        using var watcher = dispatcher.CreateWatcher(subject, () => callbackCount++);
        watcher.Watch([new PropertyReference(subject, nameof(WatcherTestSubject.Name))]);

        // Act
        dispatcher.Write(() => subject.Name = "a");
        await AsyncTestHelpers.WaitUntilAsync(() => dispatcher.DispatchCount == 1);
        dispatcher.RunAll();

        // Assert
        Assert.False(dispatcher.WasDispatchedInsideWrite);
        Assert.Equal(1, callbackCount);
    }

    [Fact]
    public async Task WhenWatchedPropertyIsWrittenBeforeTheRun_ThenWritesAreCoalesced()
    {
        // Arrange
        var (subject, dispatcher) = CreateSubjectAndDispatcher();
        var callbackCount = 0;
        using var watcher = dispatcher.CreateWatcher(subject, () => callbackCount++);
        watcher.Watch([new PropertyReference(subject, nameof(WatcherTestSubject.Name))]);

        // Act
        dispatcher.Write(() => subject.Name = "a");
        dispatcher.Write(() => subject.Name = "b");
        dispatcher.Write(() => subject.Name = "c");
        await AsyncTestHelpers.WaitUntilAsync(() => dispatcher.DispatchCount == 1);
        dispatcher.RunAll();

        dispatcher.Write(() => subject.Name = "d");
        await AsyncTestHelpers.WaitUntilAsync(() => dispatcher.DispatchCount == 2);
        dispatcher.RunAll();

        // Assert - writes before a run share it, a write after it schedules another
        Assert.Equal(2, callbackCount);
    }

    [Fact]
    public async Task WhenCallbackThrows_ThenErrorIsReportedAndWatchingContinues()
    {
        // Arrange
        var (subject, dispatcher) = CreateSubjectAndDispatcher();
        var exception = new InvalidOperationException("Test");
        using var watcher = dispatcher.CreateWatcher(subject, () => throw exception);
        watcher.Watch([new PropertyReference(subject, nameof(WatcherTestSubject.Name))]);

        // Act
        dispatcher.Write(() => subject.Name = "a");
        await AsyncTestHelpers.WaitUntilAsync(() => dispatcher.DispatchCount == 1);
        dispatcher.RunAll();

        dispatcher.Write(() => subject.Name = "b");
        await AsyncTestHelpers.WaitUntilAsync(() => dispatcher.DispatchCount == 2);
        dispatcher.RunAll();

        // Assert
        Assert.Equal(2, dispatcher.Errors.Count);
        Assert.All(dispatcher.Errors, error => Assert.Same(exception, error));
    }

    [Fact]
    public async Task WhenDisposedBeforeTheRun_ThenCallbackIsNotInvoked()
    {
        // Arrange
        var (subject, dispatcher) = CreateSubjectAndDispatcher();
        var callbackCount = 0;
        var watcher = dispatcher.CreateWatcher(subject, () => callbackCount++);
        watcher.Watch([new PropertyReference(subject, nameof(WatcherTestSubject.Name))]);
        dispatcher.Write(() => subject.Name = "a");
        await AsyncTestHelpers.WaitUntilAsync(() => dispatcher.DispatchCount == 1);

        // Act
        watcher.Dispose();
        dispatcher.RunAll();

        // Assert
        Assert.Equal(0, callbackCount);
    }

    [Fact]
    public async Task WhenInvalidated_ThenCallbackIsDispatched()
    {
        // Arrange
        var (subject, dispatcher) = CreateSubjectAndDispatcher();
        var callbackCount = 0;
        using var watcher = dispatcher.CreateWatcher(subject, () => callbackCount++);

        // Act
        watcher.Invalidate();
        await AsyncTestHelpers.WaitUntilAsync(() => dispatcher.DispatchCount == 1);
        dispatcher.RunAll();

        // Assert
        Assert.Equal(1, callbackCount);
    }

    private static (WatcherTestSubject Subject, FakeDispatcher Dispatcher) CreateSubjectAndDispatcher()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        return (new WatcherTestSubject(context), new FakeDispatcher());
    }

    /// <summary>
    /// Queues dispatched work until the test runs it, and records whether it was dispatched from inside a write.
    /// </summary>
    private sealed class FakeDispatcher
    {
        private readonly ConcurrentQueue<Action> _queue = new();
        private readonly ThreadLocal<bool> _isWriting = new();
        private int _dispatchCount;
        private volatile bool _wasDispatchedInsideWrite;

        public int DispatchCount => Volatile.Read(ref _dispatchCount);

        public bool WasDispatchedInsideWrite => _wasDispatchedInsideWrite;

        public ConcurrentQueue<Exception> Errors { get; } = new();

        public PropertyChangeWatcher CreateWatcher(IInterceptorSubject subject, Action onChanged)
            => new(subject.Context, Dispatch, onChanged, Errors.Enqueue);

        public void Write(Action write)
        {
            _isWriting.Value = true;
            try
            {
                write();
            }
            finally
            {
                _isWriting.Value = false;
            }
        }

        public void RunAll()
        {
            while (_queue.TryDequeue(out var action))
            {
                action();
            }
        }

        private Task Dispatch(Action action)
        {
            if (_isWriting.Value)
            {
                _wasDispatchedInsideWrite = true;
            }

            _queue.Enqueue(action);
            Interlocked.Increment(ref _dispatchCount);
            return Task.CompletedTask;
        }
    }
}

[InterceptorSubject]
public partial class WatcherTestSubject
{
    public partial string? Name { get; set; }
}
