using System.Collections.Concurrent;
using HomeBlaze.Storage.Internal;
using Namotion.Interceptor.Testing;

namespace HomeBlaze.Storage.Tests;

public class StorageFileWatcherTests
{
    private static readonly string BasePath = Path.Combine(Path.GetTempPath(), "homeblaze-watcher");
    private static readonly TimeSpan WatcherTimeout = TimeSpan.FromSeconds(20);

    private readonly List<(string Path, string? OtherPath)> _changes = [];

    [Fact]
    public void WhenFileChanges_ThenItsRelativePathIsReported()
    {
        // Arrange
        using var watcher = CreateWatcher();

        // Act
        watcher.SimulateFileEvent(new FileSystemEventArgs(
            WatcherChangeTypes.Changed, Path.Combine(BasePath, "Docs"), "Readme.md"));

        // Assert
        Assert.Equal([("Docs/Readme.md", null)], _changes);
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
        Assert.Equal([("New.md", "Old.md")], _changes);
    }

    [Theory]
    [InlineData("Home.md", "Home.md.tmp")]
    [InlineData("Home.md~", "Home.md")]
    public void WhenRenameTouchesOneIgnoredPath_ThenItIsStillReported(string newName, string oldName)
    {
        // Arrange
        using var watcher = CreateWatcher();

        // Act
        watcher.SimulateFileEvent(new RenamedEventArgs(WatcherChangeTypes.Renamed, BasePath, newName, oldName));

        // Assert
        Assert.Equal([(newName, oldName)], _changes);
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
    public async Task WhenWatcherFails_ThenEventsLostIsReportedAndWatchingGoesOn()
    {
        // Arrange
        var directory = Directory.CreateTempSubdirectory("homeblaze-watcher-");
        try
        {
            var changedPaths = new ConcurrentQueue<string>();
            var eventsLostCount = 0;
            using var watcher = new StorageFileWatcher(
                directory.FullName,
                (path, _) => changedPaths.Enqueue(path),
                () => Interlocked.Increment(ref eventsLostCount));

            watcher.Start();

            // Act
            watcher.SimulateError(new InternalBufferOverflowException());
            File.WriteAllText(Path.Combine(directory.FullName, "Home.md"), "content");

            // Assert
            Assert.Equal(1, Volatile.Read(ref eventsLostCount));
            await AsyncTestHelpers.WaitUntilAsync(() => changedPaths.Contains("Home.md"), WatcherTimeout);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
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
                (_, _) => flowsOfEvents.Enqueue(flow.Value),
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
        => new(BasePath, (path, otherPath) => _changes.Add((path, otherPath)), () => { });
}
