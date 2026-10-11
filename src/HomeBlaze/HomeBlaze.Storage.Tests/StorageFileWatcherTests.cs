using System.Collections.Concurrent;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Testing;

namespace HomeBlaze.Storage.Tests;

public class StorageFileWatcherTests
{
    private static readonly string BasePath = Path.Combine(Path.GetTempPath(), "homeblaze-watcher");
    private static readonly TimeSpan WatcherTimeout = TimeSpan.FromSeconds(20);

    private readonly List<string> _changes = [];

    [Fact]
    public void WhenFileChanges_ThenItsRelativePathIsReported()
    {
        // Arrange
        using var watcher = CreateWatcher();

        // Act
        watcher.SimulateFileEvent(new FileSystemEventArgs(
            WatcherChangeTypes.Changed, Path.Combine(BasePath, "Docs"), "Readme.md"));

        // Assert
        Assert.Equal(["Docs/Readme.md"], _changes);
    }

    [Theory]
    [InlineData(".DS_Store")]
    [InlineData("Home.md~")]
    public void WhenIgnoredPathChanges_ThenNothingIsReported(string name)
    {
        // Arrange
        using var watcher = CreateWatcher();

        // Act
        watcher.SimulateFileEvent(new FileSystemEventArgs(WatcherChangeTypes.Changed, BasePath, name));

        // Assert
        Assert.Empty(_changes);
    }

    [Fact]
    public void WhenFileIsRenamed_ThenBothPathsAreReported()
    {
        // Arrange
        using var watcher = CreateWatcher();

        // Act
        watcher.SimulateFileEvent(new RenamedEventArgs(WatcherChangeTypes.Renamed, BasePath, "New.md", "Old.md"));

        // Assert
        Assert.Equal(["New.md", "Old.md"], _changes);
    }

    [Theory]
    [InlineData("Home.md", "Home.md.tmp")]
    [InlineData("Home.md~", "Home.md")]
    public void WhenRenameTouchesOneIgnoredPath_ThenTheOtherPathIsReported(string newName, string oldName)
    {
        // Arrange
        using var watcher = CreateWatcher();

        // Act
        watcher.SimulateFileEvent(new RenamedEventArgs(WatcherChangeTypes.Renamed, BasePath, newName, oldName));

        // Assert
        Assert.Equal(["Home.md"], _changes);
    }

    [Fact]
    public void WhenRenameTouchesOnlyIgnoredPaths_ThenNothingIsReported()
    {
        // Arrange
        using var watcher = CreateWatcher();

        // Act
        watcher.SimulateFileEvent(new RenamedEventArgs(WatcherChangeTypes.Renamed, BasePath, "~b.tmp", "~a.tmp"));

        // Assert
        Assert.Empty(_changes);
    }

    [Fact]
    public async Task WhenBufferOverflows_ThenEventsLostIsReportedAndWatcherStartsOver()
    {
        // Arrange
        var directory = Directory.CreateTempSubdirectory("homeblaze-watcher-");
        try
        {
            var changedPaths = new ConcurrentQueue<string>();
            var eventsLostCount = 0;
            var logger = new StartCountingLogger();
            using var watcher = new StorageFileWatcher(
                directory.FullName,
                changedPaths.Enqueue,
                () => Interlocked.Increment(ref eventsLostCount),
                logger);

            watcher.Start();

            // Act
            watcher.SimulateError(new InternalBufferOverflowException());
            File.WriteAllText(Path.Combine(directory.FullName, "Home.md"), "content");

            // Assert
            Assert.Equal(1, Volatile.Read(ref eventsLostCount));
            Assert.Equal(2, logger.StartCount);
            Assert.True(watcher.IsRunning);
            await AsyncTestHelpers.WaitUntilAsync(() => changedPaths.Contains("Home.md"), WatcherTimeout);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void WhenWatcherThatIsNotRunningRaisesError_ThenEventsLostIsReportedAndNoWatcherIsStarted()
    {
        // Arrange: on Linux a watcher raises an error from within its own start, before it counts as running.
        var directory = Directory.CreateTempSubdirectory("homeblaze-watcher-");
        try
        {
            var eventsLostCount = 0;
            var logger = new StartCountingLogger();
            using var watcher = new StorageFileWatcher(directory.FullName, _ => { }, () => eventsLostCount++, logger);
            watcher.Start();

            // Act
            watcher.SimulateError(new InternalBufferOverflowException(), isRaisedByRunningWatcher: false);

            // Assert
            Assert.Equal(1, eventsLostCount);
            Assert.Equal(1, logger.StartCount);
            Assert.True(watcher.IsRunning);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task WhenWatcherFailsForAnotherReasonThanBufferOverflow_ThenEventsLostIsReportedAndItStaysStoppedUntilStartedAgain()
    {
        // Arrange
        var directory = Directory.CreateTempSubdirectory("homeblaze-watcher-");
        try
        {
            var changedPaths = new ConcurrentQueue<string>();
            var eventsLostCount = 0;
            var logger = new StartCountingLogger();
            using var watcher = new StorageFileWatcher(
                directory.FullName,
                changedPaths.Enqueue,
                () => Interlocked.Increment(ref eventsLostCount),
                logger);

            watcher.Start();

            // Act
            watcher.SimulateError(new IOException("The watch could not be added."));
            var isRunningAfterError = watcher.IsRunning;
            var startCountAfterError = logger.StartCount;
            watcher.Start();
            File.WriteAllText(Path.Combine(directory.FullName, "Home.md"), "content");

            // Assert
            Assert.Equal(1, Volatile.Read(ref eventsLostCount));
            Assert.False(isRunningAfterError);
            Assert.Equal(1, startCountAfterError);
            Assert.True(watcher.IsRunning);
            await AsyncTestHelpers.WaitUntilAsync(() => changedPaths.Contains("Home.md"), WatcherTimeout);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void WhenWatcherCannotStart_ThenItIsNotRunning()
    {
        // Arrange
        using var watcher = new StorageFileWatcher(Path.Combine(BasePath, "Missing"), _ => { }, () => { });

        // Act
        var exception = Record.Exception(watcher.Start);

        // Assert
        Assert.NotNull(exception);
        Assert.False(watcher.IsRunning);
    }

    [Fact]
    public async Task WhenWatcherIsStartedWithinFlow_ThenItsEventsDoNotCarryThatFlow()
    {
        // Arrange
        var directory = Directory.CreateTempSubdirectory("homeblaze-watcher-");
        try
        {
            var flow = new AsyncLocal<string?> { Value = "creator" };
            var flowsOfEvents = new ConcurrentQueue<string?>();
            using var watcher = new StorageFileWatcher(
                directory.FullName,
                _ => flowsOfEvents.Enqueue(flow.Value),
                () => { });

            watcher.Start();

            // Act
            File.WriteAllText(Path.Combine(directory.FullName, "Home.md"), "content");

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => !flowsOfEvents.IsEmpty, WatcherTimeout);
            Assert.All(flowsOfEvents, Assert.Null);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    // Not started: the events are simulated, so no file system watcher is needed.
    private StorageFileWatcher CreateWatcher()
        => new(BasePath, _changes.Add, () => { });

    // The watcher shows no other sign of a start: it logs one information each time it has started.
    private sealed class StartCountingLogger : ILogger
    {
        private int _startCount;

        public int StartCount => Volatile.Read(ref _startCount);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Information)
            {
                Interlocked.Increment(ref _startCount);
            }
        }
    }
}
