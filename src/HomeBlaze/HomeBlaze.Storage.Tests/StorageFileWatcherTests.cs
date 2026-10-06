using HomeBlaze.Storage.Internal;

namespace HomeBlaze.Storage.Tests;

public class StorageFileWatcherTests : IDisposable
{
    private readonly DirectoryInfo _storageDirectory = Directory.CreateTempSubdirectory("homeblaze-storagefilewatcher-");

    [Fact]
    public void WhenDisposedThenFileEventArrives_ThenNoExceptionIsThrown()
    {
        // Arrange
        var watcher = new StorageFileWatcher(
            _storageDirectory.FullName,
            _ => Task.CompletedTask,
            () => Task.CompletedTask);

        watcher.Start();
        watcher.Dispose();

        // Act
        var exception = Record.Exception(() => watcher.SimulateFileEvent(
            new FileSystemEventArgs(WatcherChangeTypes.Changed, _storageDirectory.FullName, "test.txt")));

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public void WhenDisposedThenWatcherErrorOccurs_ThenNoNewWatcherIsCreated()
    {
        // Arrange
        var watcher = new StorageFileWatcher(
            _storageDirectory.FullName,
            _ => Task.CompletedTask,
            () => Task.CompletedTask);

        watcher.Start();
        watcher.Dispose();

        // Act
        var exception = Record.Exception(() => watcher.SimulateWatcherError(new IOException("buffer overflow")));

        // Assert
        Assert.Null(exception);
        Assert.Null(watcher.Watcher);
    }

    public void Dispose()
    {
        _storageDirectory.Delete(recursive: true);
    }
}
