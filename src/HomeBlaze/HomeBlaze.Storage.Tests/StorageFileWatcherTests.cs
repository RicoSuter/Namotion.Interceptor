using HomeBlaze.Storage.Internal;

namespace HomeBlaze.Storage.Tests;

public class StorageFileWatcherTests
{
    private static readonly string BasePath = Path.Combine(Path.GetTempPath(), "homeblaze-watcher");
    private static readonly TimeSpan CoalesceWindow = TimeSpan.FromMilliseconds(500);

    private readonly ManualTimeProvider _timeProvider = new();
    private readonly List<FileSystemEventArgs> _processedEvents = [];

    [Fact]
    public void WhenFileIsRenamedFromTempFile_ThenEventIsCreatedWithOriginalCasing()
    {
        // Arrange
        var renamed = new RenamedEventArgs(WatcherChangeTypes.Renamed, BasePath, "Notes.md", "Notes.md.tmp");

        // Act
        var result = Coalesce(renamed);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(WatcherChangeTypes.Created, result.ChangeType);
        Assert.Equal(Path.Combine(BasePath, "Notes.md"), result.FullPath);
    }

    [Fact]
    public void WhenFileIsCreatedThenDeleted_ThenEventIsDeleted()
    {
        // Arrange
        var created = Event(WatcherChangeTypes.Created, "Home.md");
        var changed = Event(WatcherChangeTypes.Changed, "Home.md");
        var deleted = Event(WatcherChangeTypes.Deleted, "Home.md");

        // Act
        var result = Coalesce(created, changed, deleted);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(WatcherChangeTypes.Deleted, result.ChangeType);
        Assert.Equal(Path.Combine(BasePath, "Home.md"), result.FullPath);
    }

    [Fact]
    public void WhenFileIsDeletedThenCreated_ThenEventIsCreatedWithOriginalCasing()
    {
        // Arrange
        var deleted = Event(WatcherChangeTypes.Deleted, "Home.md");
        var created = Event(WatcherChangeTypes.Created, "Home.md");

        // Act
        var result = Coalesce(deleted, created);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(WatcherChangeTypes.Created, result.ChangeType);
        Assert.Equal(Path.Combine(BasePath, "Home.md"), result.FullPath);
    }

    [Fact]
    public void WhenFileIsDeletedThenRenamedFromTempFile_ThenEventIsCreated()
    {
        // Arrange
        var deleted = Event(WatcherChangeTypes.Deleted, "Home.md");
        var renamed = new RenamedEventArgs(WatcherChangeTypes.Renamed, BasePath, "Home.md", "~Home.md");

        // Act
        var result = Coalesce(deleted, renamed);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(WatcherChangeTypes.Created, result.ChangeType);
        Assert.Equal(Path.Combine(BasePath, "Home.md"), result.FullPath);
    }

    [Fact]
    public void WhenDirectoryIsCreatedThenChanged_ThenEventIsCreated()
    {
        // Arrange
        var created = Event(WatcherChangeTypes.Created, "Docs");
        var changed = Event(WatcherChangeTypes.Changed, "Docs");

        // Act
        var result = Coalesce(created, changed);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(WatcherChangeTypes.Created, result.ChangeType);
    }

    [Fact]
    public void WhenFileOnlyChanges_ThenEventIsChanged()
    {
        // Arrange
        var first = Event(WatcherChangeTypes.Changed, "Home.md");
        var second = Event(WatcherChangeTypes.Changed, "Home.md");

        // Act
        var result = Coalesce(first, second);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(WatcherChangeTypes.Changed, result.ChangeType);
    }

    [Fact]
    public void WhenFileIsRenamed_ThenRenameIsKept()
    {
        // Arrange
        var renamed = new RenamedEventArgs(WatcherChangeTypes.Renamed, BasePath, "New.md", "Old.md");
        var changed = Event(WatcherChangeTypes.Changed, "New.md");

        // Act
        var result = Coalesce(renamed, changed);

        // Assert
        var renamedEvent = Assert.IsType<RenamedEventArgs>(result);
        Assert.Equal(Path.Combine(BasePath, "Old.md"), renamedEvent.OldFullPath);
        Assert.Equal(Path.Combine(BasePath, "New.md"), renamedEvent.FullPath);
    }

    [Theory]
    [InlineData("~Home.md")]
    [InlineData("Home.md~")]
    [InlineData("Home.md.tmp")]
    [InlineData("Home.tmp.md")]
    public void WhenTempFileChanges_ThenEventIsIgnored(string fileName)
    {
        // Arrange
        var created = Event(WatcherChangeTypes.Created, fileName);
        var deleted = Event(WatcherChangeTypes.Deleted, fileName);

        // Act
        var result = Coalesce(created, deleted);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenFileIsRenamedToTempFile_ThenEventIsDeletedForOldPath()
    {
        // Arrange
        var renamed = new RenamedEventArgs(WatcherChangeTypes.Renamed, BasePath, "Home.md~", "Home.md");

        // Act
        var result = Coalesce(renamed);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(WatcherChangeTypes.Deleted, result.ChangeType);
        Assert.Equal(Path.Combine(BasePath, "Home.md"), result.FullPath);
    }

    [Fact]
    public void WhenWatcherRestarts_ThenEachEventIsStillHandledOnce()
    {
        // Arrange
        var directory = Directory.CreateTempSubdirectory("homeblaze-watcher-");
        try
        {
            using var watcher = new StorageFileWatcher(
                directory.FullName,
                e =>
                {
                    _processedEvents.Add(e);
                    return Task.CompletedTask;
                },
                () => Task.CompletedTask,
                timeProvider: _timeProvider);

            watcher.Start();
            watcher.Restart();
            watcher.Restart();

            // Act
            watcher.SimulateFileEvent(new FileSystemEventArgs(WatcherChangeTypes.Changed, directory.FullName, "Home.md"));
            _timeProvider.Advance(CoalesceWindow);

            // Assert
            Assert.Single(_processedEvents);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void WhenFileIsDeletedAndCreatedWithinWindow_ThenSingleCreatedEventIsProcessed()
    {
        // Arrange
        using var watcher = CreateWatcher();

        // Act
        watcher.SimulateFileEvent(Event(WatcherChangeTypes.Deleted, "Home.md"));
        watcher.SimulateFileEvent(Event(WatcherChangeTypes.Created, "HOME.md"));
        _timeProvider.Advance(CoalesceWindow);

        // Assert
        var processedEvent = Assert.Single(_processedEvents);
        Assert.Equal(WatcherChangeTypes.Created, processedEvent.ChangeType);
        Assert.Equal(Path.Combine(BasePath, "HOME.md"), processedEvent.FullPath);
    }

    [Fact]
    public void WhenDifferentFilesChangeWithinWindow_ThenEachEventIsProcessed()
    {
        // Arrange
        using var watcher = CreateWatcher();
        var first = Event(WatcherChangeTypes.Changed, "First.md");
        var second = Event(WatcherChangeTypes.Deleted, "Second.md");

        // Act
        watcher.SimulateFileEvent(first);
        watcher.SimulateFileEvent(second);
        _timeProvider.Advance(CoalesceWindow);

        // Assert
        Assert.Equal([first, second], _processedEvents);
    }

    [Fact]
    public void WhenManyPathsGoIdle_ThenNoTimerStaysArmed()
    {
        // Arrange
        const int pathCount = 500;
        using var watcher = CreateWatcher();

        // Act
        for (var index = 0; index < pathCount; index++)
        {
            watcher.SimulateFileEvent(Event(WatcherChangeTypes.Changed, $"File{index}.md"));
        }

        _timeProvider.Advance(CoalesceWindow);

        // Assert
        Assert.Equal(pathCount, _processedEvents.Count);
        Assert.Equal(0, _timeProvider.ArmedTimerCount);
    }

    [Fact]
    public void WhenDisposedThenFileEventArrives_ThenItIsIgnored()
    {
        // Arrange
        var watcher = CreateWatcher();
        watcher.Dispose();

        // Act
        var exception = Record.Exception(() => watcher.SimulateFileEvent(Event(WatcherChangeTypes.Changed, "Home.md")));
        _timeProvider.Advance(CoalesceWindow);

        // Assert
        Assert.Null(exception);
        Assert.Empty(_processedEvents);
    }

    // Not started: the events are simulated, so no file system watcher is needed.
    private StorageFileWatcher CreateWatcher()
        => new(
            BasePath,
            e =>
            {
                _processedEvents.Add(e);
                return Task.CompletedTask;
            },
            () => Task.CompletedTask,
            timeProvider: _timeProvider);

    private static FileSystemEventArgs Event(WatcherChangeTypes changeType, string name)
        => new(changeType, BasePath, name);

    private static FileSystemEventArgs? Coalesce(params FileSystemEventArgs[] events)
        => StorageFileWatcher.CoalesceEvents(events);
}
