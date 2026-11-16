using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using HomeBlaze.Services.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace HomeBlaze.Services.Tests.Diagnostics;

public class FirstChanceExceptionLoggerTests
{
    [Fact]
    public void WhenDebugIsDisabled_ThenWritesNothing()
    {
        // Arrange
        var logger = new RecordingLogger(LogLevel.Information);
        var firstChanceLogger = new FirstChanceExceptionLogger(logger, new FakeTimeProvider());

        // Act
        firstChanceLogger.Log(ThrowFromSiteA());

        // Assert
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void WhenDebugIsEnabled_ThenWritesEntryWithTypeThrowSiteAndCallStack()
    {
        // Arrange
        var logger = new RecordingLogger(LogLevel.Debug);
        var firstChanceLogger = new FirstChanceExceptionLogger(logger, new FakeTimeProvider());
        var exception = ThrowFromSiteA();

        // Act
        firstChanceLogger.Log(exception);

        // Assert
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Same(exception, entry.Exception);
        Assert.Equal(typeof(InvalidOperationException).FullName, entry.Properties["ExceptionType"]);
        Assert.Equal($"{typeof(FirstChanceExceptionLoggerTests).FullName}.{nameof(ThrowFromSiteA)}", entry.Properties["ThrowSite"]);
        Assert.Equal("Site A", entry.Properties["ExceptionMessage"]);
        Assert.Equal(0, entry.Properties["SkippedCount"]);
        var callStack = Assert.IsType<string>(entry.Properties["CallStack"]);
        // The call stack starts at the caller of Log, without the frame of Log itself.
        Assert.StartsWith(
            $"   at {typeof(FirstChanceExceptionLoggerTests).FullName}.{nameof(WhenDebugIsEnabled_ThenWritesEntryWithTypeThrowSiteAndCallStack)}()",
            callStack);
    }

    [Fact]
    public void WhenExceptionHasNoThrowSite_ThenThrowSiteIsUnknown()
    {
        // Arrange
        var logger = new RecordingLogger(LogLevel.Debug);
        var firstChanceLogger = new FirstChanceExceptionLogger(logger, new FakeTimeProvider());

        // Act
        firstChanceLogger.Log(new InvalidOperationException("Never thrown"));

        // Assert
        var entry = Assert.Single(logger.Entries);
        Assert.Equal("unknown", entry.Properties["ThrowSite"]);
    }

    [Fact]
    public void WhenSameTypeAndSiteRepeatWithinWindow_ThenSuppressesAndReportsSkippedCountAfterWindow()
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
        var entryCountWithinWindow = logger.Entries.Count;
        timeProvider.Advance(TimeSpan.FromSeconds(30));
        firstChanceLogger.Log(ThrowFromSiteA());

        // Assert
        Assert.Equal(1, entryCountWithinWindow);
        Assert.Equal(2, logger.Entries.Count);
        Assert.Equal(2, logger.Entries.Last().Properties["SkippedCount"]);
    }

    [Fact]
    public void WhenSiteOrTypeDiffers_ThenEachIsLoggedIndependently()
    {
        // Arrange
        var logger = new RecordingLogger(LogLevel.Debug);
        var firstChanceLogger = new FirstChanceExceptionLogger(logger, new FakeTimeProvider());

        // Act
        firstChanceLogger.Log(ThrowFromSiteA());
        firstChanceLogger.Log(ThrowFromSiteB());
        firstChanceLogger.Log(ThrowTimeoutFromSiteA());

        // Assert
        Assert.Equal(3, logger.Entries.Count);
        Assert.Equal(
            [
                (typeof(InvalidOperationException).FullName, nameof(ThrowFromSiteA)),
                (typeof(InvalidOperationException).FullName, nameof(ThrowFromSiteB)),
                (typeof(TimeoutException).FullName, nameof(ThrowTimeoutFromSiteA))
            ],
            logger.Entries.Select(entry => (
                (string?)entry.Properties["ExceptionType"],
                ((string)entry.Properties["ThrowSite"]!).Split('.').Last())));
    }

    [Fact]
    public void WhenLoggerThrowsAndRaisesFirstChanceAgain_ThenDoesNotRecurseOrThrow()
    {
        // Arrange
        FirstChanceExceptionLogger? firstChanceLogger = null;
        var logCallCount = 0;
        var logger = new RecordingLogger(LogLevel.Debug, onLog: () =>
        {
            logCallCount++;
            // Stands in for the handler being raised again by the exception thrown below.
            firstChanceLogger!.Log(ThrowFromSiteB());
            throw new InvalidOperationException("Logger failed");
        });
        firstChanceLogger = new FirstChanceExceptionLogger(logger, new FakeTimeProvider());

        // Act
        var exception = Record.Exception(() => firstChanceLogger.Log(ThrowFromSiteA()));

        // Assert
        Assert.Null(exception);
        Assert.Equal(1, logCallCount);
    }

    [Fact]
    public void WhenKeyLimitIsReached_ThenNewKeysAreNotLoggedAndKnownKeysStillAre()
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

        // Assert
        Assert.Equal(
            ["FirstChanceException", "FirstChanceException", "FirstChanceExceptionKeyLimitReached", "FirstChanceException"],
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
        await firstChanceLogger.StartAsync(CancellationToken.None);
        ThrowAndCatch(messageWhileStarted);
        await firstChanceLogger.StopAsync(CancellationToken.None);
        ThrowAndCatch(messageAfterStop);

        // Assert
        var entries = logger.Entries;
        var entry = Assert.Single(entries, candidate => Equals(candidate.Properties.GetValueOrDefault("ExceptionMessage"), messageWhileStarted));
        Assert.DoesNotContain(entries, candidate => Equals(candidate.Properties.GetValueOrDefault("ExceptionMessage"), messageAfterStop));
        // The call stack starts at the throwing method, without the frames of the event handler.
        Assert.StartsWith(
            $"   at {typeof(FirstChanceExceptionLoggerTests).FullName}.{nameof(ThrowAndCatch)}(",
            (string)entry.Properties["CallStack"]!);
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
        Exception? Exception);

    private sealed class RecordingLogger(LogLevel minimumLevel, Action? onLog = null) : ILogger<FirstChanceExceptionLogger>
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IReadOnlyList<LogEntry> Entries => _entries.ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimumLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            onLog?.Invoke();
            var properties = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.ToDictionary(pair => pair.Key, pair => pair.Value)
                : new Dictionary<string, object?>();
            _entries.Enqueue(new LogEntry(logLevel, eventId.Name, properties, exception));
        }
    }
}
