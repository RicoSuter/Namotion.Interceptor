using System.Collections.Concurrent;
using HomeBlaze.Storage.Internal;
using Namotion.Interceptor.Testing;

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

    [Fact]
    public async Task WhenWatcherRestartsAfterError_ThenEachFileEventIsProcessedOnce()
    {
        // Arrange
        var processedEvents = new ConcurrentQueue<FileSystemEventArgs>();
        using var watcher = new StorageFileWatcher(
            _storageDirectory.FullName,
            e =>
            {
                processedEvents.Enqueue(e);
                return Task.CompletedTask;
            },
            () => Task.CompletedTask);

        watcher.Start();
        watcher.SimulateWatcherError(new IOException("buffer overflow"));

        var firstEvent = new FileSystemEventArgs(WatcherChangeTypes.Changed, _storageDirectory.FullName, "test.txt");
        var secondEvent = new FileSystemEventArgs(WatcherChangeTypes.Changed, _storageDirectory.FullName, "test.txt");

        // Act
        watcher.SimulateFileEvent(firstEvent);
        await AsyncTestHelpers.WaitUntilAsync(() => processedEvents.Contains(firstEvent));

        // A later event on the same path is delivered after every pending delivery of the first one.
        watcher.SimulateFileEvent(secondEvent);
        await AsyncTestHelpers.WaitUntilAsync(() => processedEvents.Contains(secondEvent));

        // Assert
        Assert.Single(processedEvents, e => ReferenceEquals(e, firstEvent));
    }

    public void Dispose()
    {
        _storageDirectory.Delete(recursive: true);
    }
}
