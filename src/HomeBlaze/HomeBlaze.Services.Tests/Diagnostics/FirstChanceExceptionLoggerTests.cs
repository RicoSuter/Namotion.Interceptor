using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using HomeBlaze.Services.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Namotion.Interceptor.Testing;
using Xunit;

namespace HomeBlaze.Services.Tests.Diagnostics;

public class FirstChanceExceptionLoggerTests
{
    private const string ExceptionEvent = "FirstChanceException";

    private static readonly string TestClassName = typeof(FirstChanceExceptionLoggerTests).FullName!;

    [Fact]
    public async Task WhenDebugIsDisabled_ThenWritesNothing()
    {
        // Arrange
        var logger = new RecordingLogger(LogLevel.Information);
        using var firstChanceLogger = CreateStarted(logger, new FakeTimeProvider());
        var exception = ThrowFromSiteA(firstChanceLogger);

        // Act
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        firstChanceLogger.Log(exception);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        logger.MinimumLevel = LogLevel.Debug;
        ThrowFromSiteA(firstChanceLogger);
        await WaitForEntryCountAsync(logger, 1);

        // Assert
        Assert.Equal(0, allocated);
        // Entries are written in order, so one queued while disabled would show up before this one,
        // and an exception counted while disabled would show up as skipped.
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(0, entry.Properties["SkippedCount"]);
    }

    [Fact]
    public async Task WhenDebugIsEnabled_ThenWritesEntryWithTypeThrowSiteAndCallStack()
    {
        // Arrange
        var logger = new RecordingLogger(LogLevel.Debug);
        using var firstChanceLogger = CreateStarted(logger, new FakeTimeProvider());

        // Act
        var exception = ThrowFromSiteA(firstChanceLogger);
        await WaitForEntryCountAsync(logger, 1);

        // Assert
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Same(exception, entry.Exception);
        Assert.Equal(typeof(InvalidOperationException).FullName, entry.Properties["ExceptionType"]);
        Assert.Equal(ThrowSiteOf(nameof(ThrowFromSiteA)), entry.Properties["ThrowSite"]);
        Assert.Equal(ThrowSiteOf(nameof(ThrowFromSiteA)), entry.Properties["ThrowingFrame"]);
        Assert.Equal("Site A", entry.Properties["ExceptionMessage"]);
        Assert.Equal(0, entry.Properties["SkippedCount"]);
        var callStack = Assert.IsType<string>(entry.Properties["CallStack"]);
        // The call stack starts at the caller of Log, without the frame of Log itself.
        Assert.StartsWith($"   at {TestClassName}.{nameof(ThrowFromSiteA)}(", callStack);
    }

    [Fact]
    public async Task WhenEntryIsQueued_ThenLoggerIsCalledOffTheThrowingThread()
    {
        // Arrange
        var logger = new RecordingLogger(LogLevel.Debug);
        using var firstChanceLogger = CreateStarted(logger, new FakeTimeProvider());
        var throwingThreadId = Environment.CurrentManagedThreadId;

        // Act
        ThrowFromSiteA(firstChanceLogger);
        await WaitForEntryCountAsync(logger, 1);

        // Assert
        var entry = Assert.Single(logger.Entries);
        Assert.NotEqual(throwingThreadId, entry.ThreadId);
    }

    [Fact]
    public async Task WhenExceptionWasNeverThrown_ThenThrowingFrameIsUnknownAndThrowSiteIsTheCaller()
    {
        // Arrange
        var logger = new RecordingLogger(LogLevel.Debug);
        using var firstChanceLogger = CreateStarted(logger, new FakeTimeProvider());

        // Act
        LogFromHelper(firstChanceLogger, new InvalidOperationException("Never thrown"));
        await WaitForEntryCountAsync(logger, 1);

        // Assert
        var entry = Assert.Single(logger.Entries);
        Assert.Equal($"{TestClassName}.{nameof(LogFromHelper)}(FirstChanceExceptionLogger firstChanceLogger, Exception exception)", entry.Properties["ThrowSite"]);
        Assert.Equal("unknown", entry.Properties["ThrowingFrame"]);
    }

    [Fact]
    public async Task WhenNoCallerIsOutsideTheFramework_ThenThrowSiteIsTheThrowingFrameOrUnknown()
    {
        // Arrange
        var logger = new RecordingLogger(LogLevel.Debug);
        using var firstChanceLogger = CreateStarted(logger, new FakeTimeProvider());
        var thrown = Thrown(new InvalidOperationException("Thrown"));
        // More frames than the stack holds leaves no caller, as for a throw from framework code only.
        const int skippedCallerFrames = 10_000;

        // Act
        firstChanceLogger.Log(thrown, skippedCallerFrames);
        firstChanceLogger.Log(new TimeoutException("Never thrown"), skippedCallerFrames);
        await WaitForEntryCountAsync(logger, 2);

        // Assert
        Assert.Equal(
            [
                ($"{TestClassName}.{nameof(Thrown)}(Exception exception)", $"{TestClassName}.{nameof(Thrown)}(Exception exception)"),
                ("unknown", "unknown")
            ],
            logger.Entries.Select(entry => (entry.Properties["ThrowSite"], entry.Properties["ThrowingFrame"])));
    }

    [Fact]
    public async Task WhenSameTypeAndSiteRepeatWithinWindow_ThenSuppressesAndReportsSkippedCountAfterWindow()
    {
        // Arrange
        var logger = new RecordingLogger(LogLevel.Debug);
        var timeProvider = new FakeTimeProvider();
        using var firstChanceLogger = CreateStarted(logger, timeProvider);
        ThrowFromSiteA(firstChanceLogger);

        // Act
        timeProvider.Advance(TimeSpan.FromSeconds(30));
        ThrowFromSiteA(firstChanceLogger);
        ThrowFromSiteA(firstChanceLogger);
        timeProvider.Advance(TimeSpan.FromSeconds(30));
        ThrowFromSiteA(firstChanceLogger);
        await WaitForEntryCountAsync(logger, 2);

        // Assert
        // Entries are written in order, so a suppressed exception that was queued would show up before the last one.
        Assert.Equal([0, 2], logger.Entries.Select(entry => entry.Properties["SkippedCount"]));
    }

    [Fact]
    public async Task WhenSameInstanceIsRaisedAgain_ThenIsIgnoredAndNotCountedAsSkipped()
    {
        // Arrange
        var logger = new RecordingLogger(LogLevel.Debug);
        var timeProvider = new FakeTimeProvider();
        using var firstChanceLogger = CreateStarted(logger, timeProvider);
        var exception = ThrowFromSiteA(firstChanceLogger);

        // Act
        firstChanceLogger.Log(exception);
        timeProvider.Advance(FirstChanceExceptionLogger.Window);
        firstChanceLogger.Log(exception);
        ThrowFromSiteA(firstChanceLogger);
        await WaitForEntryCountAsync(logger, 2);

        // Assert
        Assert.Equal([0, 0], logger.Entries.Select(entry => entry.Properties["SkippedCount"]));
    }

    [Fact]
    public async Task WhenSameKeyIsThrownFromSeveralThreadsAtOnce_ThenWritesOneEntryAndCountsEverySkip()
    {
        // Arrange
        const int threadCount = 8;
        const int exceptionsPerThread = 50;
        var logger = new RecordingLogger(LogLevel.Debug);
        var timeProvider = new FakeTimeProvider();
        using var firstChanceLogger = CreateStarted(logger, timeProvider);
        using var barrier = new Barrier(threadCount);

        // Act
        await Task.WhenAll(Enumerable.Range(0, threadCount).Select(_ => Task.Factory.StartNew(
            () =>
            {
                barrier.SignalAndWait();
                for (var index = 0; index < exceptionsPerThread; index++)
                {
                    ThrowFromSiteA(firstChanceLogger);
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default)));
        timeProvider.Advance(FirstChanceExceptionLogger.Window);
        ThrowFromSiteA(firstChanceLogger);
        await WaitForEntryCountAsync(logger, 2);

        // Assert
        Assert.Equal([0, threadCount * exceptionsPerThread - 1], logger.Entries.Select(entry => entry.Properties["SkippedCount"]));
    }

    [Fact]
    public async Task WhenSiteOrTypeDiffers_ThenEachIsLoggedIndependently()
    {
        // Arrange
        var logger = new RecordingLogger(LogLevel.Debug);
        using var firstChanceLogger = CreateStarted(logger, new FakeTimeProvider());

        // Act
        ThrowFromSiteA(firstChanceLogger);
        ThrowFromSiteB(firstChanceLogger);
        ThrowTimeoutFromSiteA(firstChanceLogger);
        await WaitForEntryCountAsync(logger, 3);

        // Assert
        Assert.Equal(
            [
                (typeof(InvalidOperationException).FullName, ThrowSiteOf(nameof(ThrowFromSiteA))),
                (typeof(InvalidOperationException).FullName, ThrowSiteOf(nameof(ThrowFromSiteB))),
                (typeof(TimeoutException).FullName, ThrowSiteOf(nameof(ThrowTimeoutFromSiteA)))
            ],
            logger.Entries.Select(entry => (entry.Properties["ExceptionType"], entry.Properties["ThrowSite"])));
    }

    [Fact]
    public async Task WhenLoggerThrowsAndRaisesFirstChanceAgain_ThenDoesNotRequeueOrStopWriting()
    {
        // Arrange
        FirstChanceExceptionLogger? firstChanceLogger = null;
        var logCallCount = 0;
        var logger = new RecordingLogger(LogLevel.Debug, onLog: () =>
        {
            if (Interlocked.Increment(ref logCallCount) == 1)
            {
                // Stands in for the handler being raised again on the consumer by the exception thrown below.
                ThrowFromSiteB(firstChanceLogger!);
                throw new InvalidOperationException("Logger failed");
            }
        });
        using var startedLogger = CreateStarted(logger, new FakeTimeProvider());
        firstChanceLogger = startedLogger;

        // Act
        var exception = Record.Exception(() => ThrowFromSiteA(startedLogger));
        ThrowTimeoutFromSiteA(startedLogger);
        await WaitForEntryCountAsync(logger, 1);

        // Assert
        Assert.Null(exception);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(typeof(TimeoutException).FullName, entry.Properties["ExceptionType"]);
        Assert.Equal(2, logCallCount);
    }

    [Fact]
    public async Task WhenQueueIsFull_ThenDropsEntriesAndReportsDroppedCountAndSkips()
    {
        // Arrange
        using var consumerBlocked = new ManualResetEventSlim();
        using var releaseConsumer = new ManualResetEventSlim();
        var logCallCount = 0;
        var logger = new RecordingLogger(LogLevel.Debug, onLog: () =>
        {
            if (Interlocked.Increment(ref logCallCount) == 1)
            {
                consumerBlocked.Set();
                releaseConsumer.Wait(TestTimeouts.Default);
            }
        });
        var timeProvider = new FakeTimeProvider();
        using var firstChanceLogger = CreateStarted(logger, timeProvider, queueCapacity: 2);
        ThrowAndLog(firstChanceLogger, new InvalidOperationException("Written while blocking"));
        Assert.True(consumerBlocked.Wait(TestTimeouts.Default));

        // Act
        ThrowAndLog(firstChanceLogger, new TimeoutException("Queued 1"));
        ThrowAndLog(firstChanceLogger, new ArgumentException("Queued 2"));
        ThrowAndLog(firstChanceLogger, new FormatException("Dropped 1"));
        ThrowAndLog(firstChanceLogger, new NotSupportedException("Dropped 2"));
        releaseConsumer.Set();
        // The queue must be empty again before the next entry, or it could be dropped as well.
        await WaitForEntryCountAsync(logger, 4);
        timeProvider.Advance(FirstChanceExceptionLogger.Window);
        ThrowAndLog(firstChanceLogger, new FormatException("After window"));
        await WaitForEntryCountAsync(logger, 5);

        // Assert
        // The drops are reported after the entry that was being written when they happened.
        Assert.Equal(
            ["Written while blocking", "FirstChanceExceptionEntriesDropped", "Queued 1", "Queued 2", "After window"],
            logger.Entries.Select(entry => entry.EventName == ExceptionEvent ? entry.Properties["ExceptionMessage"] : entry.EventName));
        Assert.Equal(2, logger.Entries[1].Properties["DroppedCount"]);
        // The dropped entry of the same type and throw site counts as skipped.
        Assert.Equal(1, logger.Entries[4].Properties["SkippedCount"]);
    }

    [Fact]
    public async Task WhenKeyLimitIsReached_ThenNewKeysAreNotLoggedAndKnownKeysStillAre()
    {
        // Arrange
        var logger = new RecordingLogger(LogLevel.Debug);
        var timeProvider = new FakeTimeProvider();
        using var firstChanceLogger = CreateStarted(logger, timeProvider, maximumKeyCount: 2);
        ThrowFromSiteA(firstChanceLogger);
        ThrowFromSiteB(firstChanceLogger);

        // Act
        ThrowTimeoutFromSiteA(firstChanceLogger);
        ThrowTimeoutFromSiteA(firstChanceLogger);
        timeProvider.Advance(FirstChanceExceptionLogger.Window);
        ThrowTimeoutFromSiteA(firstChanceLogger);
        ThrowFromSiteA(firstChanceLogger);
        await WaitForEntryCountAsync(logger, 4);

        // Assert
        Assert.Equal(
            [ExceptionEvent, ExceptionEvent, "FirstChanceExceptionKeyLimitReached", ExceptionEvent],
            logger.Entries.Select(entry => entry.EventName));
        Assert.Equal(2, logger.Entries[2].Properties["MaximumKeyCount"]);
        Assert.Equal(typeof(InvalidOperationException).FullName, logger.Entries[3].Properties["ExceptionType"]);
    }

    [Fact]
    public async Task WhenStartedAndStopped_ThenLogsExceptionsOnlyWhileStarted()
    {
        // Arrange
        // Other tests throw in parallel, so entries are matched by a message unique to this test.
        var messageWhileStarted = $"While started {Guid.NewGuid()}";
        var messageAfterStop = $"After stop {Guid.NewGuid()}";
        var logger = new RecordingLogger(LogLevel.Debug);
        using var firstChanceLogger = new FirstChanceExceptionLogger(logger);

        // Act
        await firstChanceLogger.StartingAsync(CancellationToken.None);
        try
        {
            ThrowAndCatch(messageWhileStarted);
            await AsyncTestHelpers.WaitUntilAsync(() => logger.Entries.Any(entry => HasMessage(entry, messageWhileStarted)));
        }
        finally
        {
            await firstChanceLogger.StoppedAsync(CancellationToken.None);
        }

        // The consumer has finished, so only a handler still subscribed can call IsEnabled.
        var isEnabledCallCount = logger.IsEnabledCallCount;
        ThrowAndCatch(messageAfterStop);

        // Assert
        Assert.Equal(isEnabledCallCount, logger.IsEnabledCallCount);
        var entries = logger.Entries;
        var entry = Assert.Single(entries, candidate => HasMessage(candidate, messageWhileStarted));
        Assert.DoesNotContain(entries, candidate => HasMessage(candidate, messageAfterStop));
        // The call stack starts at the throwing method, without the frames of the event handler.
        Assert.StartsWith($"   at {TestClassName}.{nameof(ThrowAndCatch)}(", (string)entry.Properties["CallStack"]!);
    }

    [Fact]
    public async Task WhenExceptionPassesThroughAwaits_ThenLogsAsyncThrowSiteOnceWithoutCountingRethrows()
    {
        // Arrange
        var logger = new RecordingLogger(LogLevel.Debug);
        var timeProvider = new FakeTimeProvider();
        using var firstChanceLogger = new FirstChanceExceptionLogger(logger, timeProvider);
        // The async method itself, not its state machine, and the same at every await it passes through.
        var throwSite = $"{TestClassName}.{nameof(ThrowAfterYieldAsync)}(String message)";

        // Act
        await firstChanceLogger.StartingAsync(CancellationToken.None);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => AwaitTwiceAsync("First"));
            timeProvider.Advance(FirstChanceExceptionLogger.Window);
            await Assert.ThrowsAsync<InvalidOperationException>(() => AwaitTwiceAsync("Second"));
            await AsyncTestHelpers.WaitUntilAsync(() => logger.Entries.Any(entry => HasMessage(entry, "Second")));
        }
        finally
        {
            await firstChanceLogger.StoppedAsync(CancellationToken.None);
        }

        // Assert
        var entries = logger.Entries.Where(entry => Equals(entry.Properties.GetValueOrDefault("ThrowSite"), throwSite)).ToArray();
        Assert.Equal(["First", "Second"], entries.Select(entry => entry.Properties["ExceptionMessage"]));
        Assert.Equal([0, 0], entries.Select(entry => entry.Properties["SkippedCount"]));
    }

    [Fact]
    public async Task WhenAwaitOfCanceledTaskThrowsInTwoMethods_ThenEachMethodIsItsOwnThrowSite()
    {
        // Arrange
        var logger = new RecordingLogger(LogLevel.Debug);
        using var firstChanceLogger = new FirstChanceExceptionLogger(logger);
        // Every awaited canceled task throws from the same framework method.
        var throwSiteA = $"{TestClassName}.{nameof(AwaitCanceledFromSiteAAsync)}()";
        var throwSiteB = $"{TestClassName}.{nameof(AwaitCanceledFromSiteBAsync)}()";

        // Act
        await firstChanceLogger.StartingAsync(CancellationToken.None);
        try
        {
            await AwaitCanceledFromSiteAAsync();
            await AwaitCanceledFromSiteBAsync();
            await AsyncTestHelpers.WaitUntilAsync(() => logger.Entries.Count(entry => IsThrowSite(entry, throwSiteA) || IsThrowSite(entry, throwSiteB)) == 2);
        }
        finally
        {
            await firstChanceLogger.StoppedAsync(CancellationToken.None);
        }

        // Assert
        foreach (var throwSite in new[] { throwSiteA, throwSiteB })
        {
            var entry = Assert.Single(logger.Entries, candidate => IsThrowSite(candidate, throwSite));
            Assert.Equal(typeof(TaskCanceledException).FullName, entry.Properties["ExceptionType"]);
            Assert.StartsWith("System.Runtime.CompilerServices.TaskAwaiter.", (string)entry.Properties["ThrowingFrame"]!);
        }
    }

    [Fact]
    public async Task WhenThrowHelperThrowsForTwoMethods_ThenEachMethodIsItsOwnThrowSite()
    {
        // Arrange
        var logger = new RecordingLogger(LogLevel.Debug);
        using var firstChanceLogger = new FirstChanceExceptionLogger(logger);
        var throwSiteA = $"{TestClassName}.{nameof(ThrowDisposedFromSiteA)}()";
        var throwSiteB = $"{TestClassName}.{nameof(ThrowDisposedFromSiteB)}()";

        // Act
        await firstChanceLogger.StartingAsync(CancellationToken.None);
        try
        {
            ThrowDisposedFromSiteA();
            ThrowDisposedFromSiteB();
            await AsyncTestHelpers.WaitUntilAsync(() => logger.Entries.Count(entry => IsThrowSite(entry, throwSiteA) || IsThrowSite(entry, throwSiteB)) == 2);
        }
        finally
        {
            await firstChanceLogger.StoppedAsync(CancellationToken.None);
        }

        // Assert
        foreach (var throwSite in new[] { throwSiteA, throwSiteB })
        {
            var entry = Assert.Single(logger.Entries, candidate => IsThrowSite(candidate, throwSite));
            Assert.Equal(typeof(ObjectDisposedException).FullName, entry.Properties["ExceptionType"]);
            Assert.StartsWith("System.", (string)entry.Properties["ThrowingFrame"]!);
        }
    }

    [Fact]
    public async Task WhenDisposedWithoutStop_ThenHandlerIsRemoved()
    {
        // Arrange
        var logger = new RecordingLogger(LogLevel.Debug);
        var firstChanceLogger = new FirstChanceExceptionLogger(logger);
        await firstChanceLogger.StartingAsync(CancellationToken.None);
        // Only calls on this thread count: the consumer and the handler raised by other tests call IsEnabled on other threads.
        var threadId = Environment.CurrentManagedThreadId;
        var callCountBeforeThrow = logger.GetIsEnabledCallCount(threadId);
        ThrowAndCatch($"Before dispose {Guid.NewGuid()}");
        var callCountAfterThrow = logger.GetIsEnabledCallCount(threadId);

        // Act
        firstChanceLogger.Dispose();
        var callCountAfterDispose = logger.GetIsEnabledCallCount(threadId);
        ThrowAndCatch($"After dispose {Guid.NewGuid()}");

        // Assert
        Assert.True(callCountAfterThrow > callCountBeforeThrow);
        Assert.Equal(callCountAfterDispose, logger.GetIsEnabledCallCount(threadId));
    }

    [Fact]
    public async Task WhenThrowingThreadHasActivity_ThenEntryIsWrittenWithoutIt()
    {
        // Arrange
        var logger = new RecordingLogger(LogLevel.Debug);
        using var activity = new Activity("Throwing thread").Start();
        using var firstChanceLogger = CreateStarted(logger, new FakeTimeProvider());

        // Act
        ThrowFromSiteA(firstChanceLogger);
        await WaitForEntryCountAsync(logger, 1);

        // Assert
        Assert.Same(activity, Activity.Current);
        var entry = Assert.Single(logger.Entries);
        Assert.Null(entry.Activity);
    }

    private static FirstChanceExceptionLogger CreateStarted(
        RecordingLogger logger,
        TimeProvider timeProvider,
        int maximumKeyCount = FirstChanceExceptionLogger.DefaultMaximumKeyCount,
        int queueCapacity = FirstChanceExceptionLogger.DefaultQueueCapacity)
    {
        // Starts the consumer without subscribing, so exceptions thrown by tests running in parallel stay out.
        var firstChanceLogger = new FirstChanceExceptionLogger(logger, timeProvider, maximumKeyCount, queueCapacity);
        firstChanceLogger.StartConsumer();
        return firstChanceLogger;
    }

    private static bool HasMessage(LogEntry entry, string message)
    {
        return Equals(entry.Properties.GetValueOrDefault("ExceptionMessage"), message);
    }

    private static bool IsThrowSite(LogEntry entry, string throwSite)
    {
        return Equals(entry.Properties.GetValueOrDefault("ThrowSite"), throwSite);
    }

    private static string ThrowSiteOf(string throwingMethodName)
    {
        return $"{TestClassName}.{throwingMethodName}(FirstChanceExceptionLogger firstChanceLogger)";
    }

    private static Task WaitForEntryCountAsync(RecordingLogger logger, int count)
    {
        return AsyncTestHelpers.WaitUntilAsync(() => logger.Entries.Count >= count);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LogFromHelper(FirstChanceExceptionLogger firstChanceLogger, Exception exception)
    {
        firstChanceLogger.Log(exception);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task AwaitTwiceAsync(string message)
    {
        await AwaitOnceAsync(message);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task AwaitOnceAsync(string message)
    {
        await ThrowAfterYieldAsync(message);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task ThrowAfterYieldAsync(string message)
    {
        await Task.Yield();
        throw new InvalidOperationException(message);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task AwaitCanceledFromSiteAAsync()
    {
        try
        {
            await Task.FromCanceled(new CancellationToken(canceled: true));
        }
        catch (OperationCanceledException)
        {
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task AwaitCanceledFromSiteBAsync()
    {
        try
        {
            await Task.FromCanceled(new CancellationToken(canceled: true));
        }
        catch (OperationCanceledException)
        {
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowDisposedFromSiteA()
    {
        try
        {
            ObjectDisposedException.ThrowIf(true, typeof(FirstChanceExceptionLoggerTests));
        }
        catch (ObjectDisposedException)
        {
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowDisposedFromSiteB()
    {
        try
        {
            ObjectDisposedException.ThrowIf(true, typeof(FirstChanceExceptionLoggerTests));
        }
        catch (ObjectDisposedException)
        {
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Exception Thrown(Exception exception)
    {
        try
        {
            throw exception;
        }
        catch (Exception caught)
        {
            return caught;
        }
    }

    // The helpers below log from their catch block, where the calling stack starts at the throwing
    // method as it does at first chance.

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Exception ThrowAndLog(FirstChanceExceptionLogger firstChanceLogger, Exception exception)
    {
        try
        {
            throw exception;
        }
        catch (Exception caught)
        {
            firstChanceLogger.Log(caught);
            return caught;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Exception ThrowFromSiteA(FirstChanceExceptionLogger firstChanceLogger)
    {
        try
        {
            throw new InvalidOperationException("Site A");
        }
        catch (Exception exception)
        {
            firstChanceLogger.Log(exception);
            return exception;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Exception ThrowFromSiteB(FirstChanceExceptionLogger firstChanceLogger)
    {
        try
        {
            throw new InvalidOperationException("Site B");
        }
        catch (Exception exception)
        {
            firstChanceLogger.Log(exception);
            return exception;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Exception ThrowTimeoutFromSiteA(FirstChanceExceptionLogger firstChanceLogger)
    {
        try
        {
            throw new TimeoutException("Site A timeout");
        }
        catch (Exception exception)
        {
            firstChanceLogger.Log(exception);
            return exception;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowAndCatch(string message)
    {
        try
        {
            throw new ArgumentException(message);
        }
        catch (ArgumentException)
        {
        }
    }

    private sealed record LogEntry(
        LogLevel Level,
        string? EventName,
        IReadOnlyDictionary<string, object?> Properties,
        Exception? Exception,
        int ThreadId,
        Activity? Activity);

    private sealed class RecordingLogger(LogLevel minimumLevel, Action? onLog = null) : ILogger<FirstChanceExceptionLogger>
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();
        private readonly ConcurrentDictionary<int, int> _isEnabledCallCountByThread = new();
        private volatile int _minimumLevel = (int)minimumLevel;
        private int _isEnabledCallCount;

        public LogLevel MinimumLevel
        {
            get => (LogLevel)_minimumLevel;
            set => _minimumLevel = (int)value;
        }

        public IReadOnlyList<LogEntry> Entries => _entries.ToArray();

        public int IsEnabledCallCount => Volatile.Read(ref _isEnabledCallCount);

        public int GetIsEnabledCallCount(int threadId) => _isEnabledCallCountByThread.GetValueOrDefault(threadId);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel)
        {
            Interlocked.Increment(ref _isEnabledCallCount);
            _isEnabledCallCountByThread.AddOrUpdate(Environment.CurrentManagedThreadId, 1, static (_, count) => count + 1);
            return logLevel >= MinimumLevel;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            onLog?.Invoke();
            var properties = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.ToDictionary(pair => pair.Key, pair => pair.Value)
                : new Dictionary<string, object?>();
            _entries.Enqueue(new LogEntry(logLevel, eventId.Name, properties, exception, Environment.CurrentManagedThreadId, Activity.Current));
        }
    }
}
