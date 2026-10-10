using HomeBlaze.Storage.Internal;

namespace HomeBlaze.Storage.Tests;

public class StorageFileWatcherTests
{
    private static readonly string BasePath = Path.Combine(Path.GetTempPath(), "homeblaze-watcher");

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
        var created = Event(WatcherChangeTypes.Created, "._Home.md");
        var changed = Event(WatcherChangeTypes.Changed, "._Home.md");
        var deleted = Event(WatcherChangeTypes.Deleted, "._Home.md");

        // Act
        var result = Coalesce(created, changed, deleted);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(WatcherChangeTypes.Deleted, result.ChangeType);
        Assert.Equal(Path.Combine(BasePath, "._Home.md"), result.FullPath);
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

    private static FileSystemEventArgs Event(WatcherChangeTypes changeType, string name)
        => new(changeType, BasePath, name);

    private static FileSystemEventArgs? Coalesce(params FileSystemEventArgs[] events)
        => StorageFileWatcher.CoalesceEvents(events);
}
