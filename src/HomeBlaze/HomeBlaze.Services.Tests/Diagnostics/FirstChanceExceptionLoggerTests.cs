using System.Collections.Concurrent;
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
        var firstChanceLogger = new FirstChanceExceptionLogger(logger, new FakeTimeProvider());

        // Act
        firstChanceLogger.Log(ThrowFromSiteA());
        logger.MinimumLevel = LogLevel.Debug;
        firstChanceLogger.Log(ThrowFromSiteB());
        await WaitForEntryCountAsync(logger, 1);

        // Assert
        var entry = Assert.Single(logger.Entries);
        Assert.Equal("Site B", entry.Properties["ExceptionMessage"]);
    }

    [Fact]
    public async Task WhenDebugIsEnabled_ThenWritesEntryWithTypeThrowSiteAndCallStack()
    {
        // Arrange
        var logger = new RecordingLogger(LogLevel.Debug);
        var firstChanceLogger = new FirstChanceExceptionLogger(logger, new FakeTimeProvider());
        var exception = ThrowFromSiteA();

        // Act
        LogFromHelper(firstChanceLogger, exception);
        await WaitForEntryCountAsync(logger, 1);

        // Assert
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Same(exception, entry.Exception);
        Assert.Equal(typeof(InvalidOperationException).FullName, entry.Properties["ExceptionType"]);
        Assert.Equal($"{TestClassName}.{nameof(ThrowFromSiteA)}()", entry.Properties["ThrowSite"]);
        Assert.Equal("Site A", entry.Properties["ExceptionMessage"]);
        Assert.Equal(0, entry.Properties["SkippedCount"]);
        var callStack = Assert.IsType<string>(entry.Properties["CallStack"]);
        // The call stack starts at the caller of Log, without the frame of Log itself.
        Assert.StartsWith($"   at {TestClassName}.{nameof(LogFromHelper)}(", callStack);
    }

    [Fact]
    public async Task WhenEntryIsQueued_ThenLoggerIsCalledOffTheThrowingThread()
    {
        // Arrange
        var logger = new RecordingLogger(LogLevel.Debug);
        var firstChanceLogger = new FirstChanceExceptionLogger(logger, new FakeTimeProvider());
        var throwingThreadId = Environment.CurrentManagedThreadId;

        // Act
        firstChanceLogger.Log(ThrowFromSiteA());
        await WaitForEntryCountAsync(logger, 1);

        // Assert
        var entry = Assert.Single(logger.Entries);
        Assert.NotEqual(throwingThreadId, entry.ThreadId);
    }

    [Fact]
    public async Task WhenExceptionHasNoThrowSite_ThenThrowSiteIsUnknown()
    {
        // Arrange
        var logger = new RecordingLogger(LogLevel.Debug);
        var firstChanceLogger = new FirstChanceExceptionLogger(logger, new FakeTimeProvider());

        // Act
        firstChanceLogger.Log(new InvalidOperationException("Never thrown"));
        await WaitForEntryCountAsync(logger, 1);

        // Assert
        var entry = Assert.Single(logger.Entries);
        Assert.Equal("unknown", entry.Properties["ThrowSite"]);
    }

    [Fact]
    public async Task WhenSameTypeAndSiteRepeatWithinWindow_ThenSuppressesAndReportsSkippedCountAfterWindow()
    {
        // Arrange
        var logger = new RecordingLogger(LogLevel.Debug);
        var timeProvider = new FakeTimeProvider();
        var firstChanceLogger = new FirstChanceExceptionLogger(logger, timeProvider);
        firstChanceLogger.Log(ThrowFromSiteA());

        // Act
        timeProvider.Advance(TimeSpan.FromSeconds(30));
        firstChanceLogger.Log(ThrowFromSiteA());
        firstChanceLogger.Log(ThrowFromSiteA());
        timeProvider.Advance(TimeSpan.FromSeconds(30));
        firstChanceLogger.Log(ThrowFromSiteA());
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
        var firstChanceLogger = new FirstChanceExceptionLogger(logger, timeProvider);
        var exception = ThrowFromSiteA();
        firstChanceLogger.Log(exception);

        // Act
        firstChanceLogger.Log(exception);
        timeProvider.Advance(FirstChanceExceptionLogger.Window);
        firstChanceLogger.Log(exception);
        firstChanceLogger.Log(ThrowFromSiteA());
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
        var firstChanceLogger = new FirstChanceExceptionLogger(logger, timeProvider);
        using var barrier = new Barrier(threadCount);

        // Act
        await Task.WhenAll(Enumerable.Range(0, threadCount).Select(_ => Task.Factory.StartNew(
            () =>
            {
                barrier.SignalAndWait();
                for (var index = 0; index < exceptionsPerThread; index++)
                {
                    firstChanceLogger.Log(ThrowFromSiteA());
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default)));
        timeProvider.Advance(FirstChanceExceptionLogger.Window);
        firstChanceLogger.Log(ThrowFromSiteA());
        await WaitForEntryCountAsync(logger, 2);

        // Assert
        Assert.Equal([0, threadCount * exceptionsPerThread - 1], logger.Entries.Select(entry => entry.Properties["SkippedCount"]));
    }

    [Fact]
    public async Task WhenSiteOrTypeDiffers_ThenEachIsLoggedIndependently()
    {
        // Arrange
        var logger = new RecordingLogger(LogLevel.Debug);
        var firstChanceLogger = new FirstChanceExceptionLogger(logger, new FakeTimeProvider());

        // Act
        firstChanceLogger.Log(ThrowFromSiteA());
        firstChanceLogger.Log(ThrowFromSiteB());
        firstChanceLogger.Log(ThrowTimeoutFromSiteA());
        await WaitForEntryCountAsync(logger, 3);

        // Assert
        Assert.Equal(
            [
                (typeof(InvalidOperationException).FullName, $"{nameof(ThrowFromSiteA)}()"),
                (typeof(InvalidOperationException).FullName, $"{nameof(ThrowFromSiteB)}()"),
                (typeof(TimeoutException).FullName, $"{nameof(ThrowTimeoutFromSiteA)}()")
            ],
            logger.Entries.Select(entry => (
                (string?)entry.Properties["ExceptionType"],
                ((string)entry.Properties["ThrowSite"]!).Split('.').Last())));
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
                firstChanceLogger!.Log(ThrowFromSiteB());
                throw new InvalidOperationException("Logger failed");
            }
        });
        firstChanceLogger = new FirstChanceExceptionLogger(logger, new FakeTimeProvider());

        // Act
        var exception = Record.Exception(() => firstChanceLogger.Log(ThrowFromSiteA()));
        firstChanceLogger.Log(ThrowTimeoutFromSiteA());
        await WaitForEntryCountAsync(logger, 1);

        // Assert
        Assert.Null(exception);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(typeof(TimeoutException).FullName, entry.Properties["ExceptionType"]);
        Assert.Equal(2, logCallCount);
    }

    [Fact]
    public async Task WhenQueueIsFull_ThenDropsEntriesAndReportsDroppedCount()
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
        var firstChanceLogger = new FirstChanceExceptionLogger(logger, new FakeTimeProvider(), queueCapacity: 2);
        firstChanceLogger.Log(Thrown(new InvalidOperationException("Written while blocking")));
        Assert.True(consumerBlocked.Wait(TestTimeouts.Default));

        // Act
        firstChanceLogger.Log(Thrown(new TimeoutException("Queued 1")));
        firstChanceLogger.Log(Thrown(new ArgumentException("Queued 2")));
        firstChanceLogger.Log(Thrown(new FormatException("Dropped 1")));
        firstChanceLogger.Log(Thrown(new NotSupportedException("Dropped 2")));
        releaseConsumer.Set();
        await WaitForEntryCountAsync(logger, 4);

        // Assert
        // The drops are reported after the entry that was being written when they happened.
        Assert.Equal(
            ["Written while blocking", "FirstChanceExceptionEntriesDropped", "Queued 1", "Queued 2"],
            logger.Entries.Select(entry => entry.EventName == ExceptionEvent ? entry.Properties["ExceptionMessage"] : entry.EventName));
        Assert.Equal(2, logger.Entries[1].Properties["DroppedCount"]);
    }

    [Fact]
    public async Task WhenKeyLimitIsReached_ThenNewKeysAreNotLoggedAndKnownKeysStillAre()
    {
        // Arrange
        var logger = new RecordingLogger(LogLevel.Debug);
        var timeProvider = new FakeTimeProvider();
        var firstChanceLogger = new FirstChanceExceptionLogger(logger, timeProvider, maximumKeyCount: 2);
        firstChanceLogger.Log(ThrowFromSiteA());
        firstChanceLogger.Log(ThrowFromSiteB());

        // Act
        firstChanceLogger.Log(ThrowTimeoutFromSiteA());
        firstChanceLogger.Log(ThrowTimeoutFromSiteA());
        timeProvider.Advance(FirstChanceExceptionLogger.Window);
        firstChanceLogger.Log(ThrowTimeoutFromSiteA());
        firstChanceLogger.Log(ThrowFromSiteA());
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
        var firstChanceLogger = new FirstChanceExceptionLogger(logger);

        // Act
        await firstChanceLogger.StartingAsync(CancellationToken.None);
        try
        {
            ThrowAndCatch(messageWhileStarted);
            await AsyncTestHelpers.WaitUntilAsync(() => logger.Entries.Any(entry => HasMessage(entry, messageWhileStarted)));
        }
        finally
        {
            // Stopped even when the wait fails, so no handler stays on the process-wide event.
            await firstChanceLogger.StoppedAsync(CancellationToken.None);
        }

        ThrowAndCatch(messageAfterStop);

        // Assert
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
        var firstChanceLogger = new FirstChanceExceptionLogger(logger, timeProvider);
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

    private static bool HasMessage(LogEntry entry, string message)
    {
        return Equals(entry.Properties.GetValueOrDefault("ExceptionMessage"), message);
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

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Exception ThrowFromSiteA()
    {
        try
        {
            throw new InvalidOperationException("Site A");
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Exception ThrowFromSiteB()
    {
        try
        {
            throw new InvalidOperationException("Site B");
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Exception ThrowTimeoutFromSiteA()
    {
        try
        {
            throw new TimeoutException("Site A timeout");
        }
        catch (Exception exception)
        {
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
        int ThreadId);

    private sealed class RecordingLogger(LogLevel minimumLevel, Action? onLog = null) : ILogger<FirstChanceExceptionLogger>
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();
        private volatile int _minimumLevel = (int)minimumLevel;

        public LogLevel MinimumLevel
        {
            get => (LogLevel)_minimumLevel;
            set => _minimumLevel = (int)value;
        }

        public IReadOnlyList<LogEntry> Entries => _entries.ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= MinimumLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            onLog?.Invoke();
            var properties = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.ToDictionary(pair => pair.Key, pair => pair.Value)
                : new Dictionary<string, object?>();
            _entries.Enqueue(new LogEntry(logLevel, eventId.Name, properties, exception, Environment.CurrentManagedThreadId));
        }
    }
}
