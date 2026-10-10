using FluentStorage.Blobs;
using HomeBlaze.Services;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Everything that belongs to one connection of a container to its storage. A reconnect replaces it as a whole,
/// so work of an ended connection never touches the index or the client of the connection after it.
/// </summary>
internal sealed class StorageConnection : IDisposable
{
    private readonly CancellationTokenSource _endedSource = new();
    private readonly Lock _fileWatcherLock = new();
    private readonly TimeLimitedBlobStorage _client;
    private readonly ILogger? _logger;

    private StorageFileWatcher? _fileWatcher;

    public StorageConnection(
        IBlobStorage client,
        string? storageDirectory,
        FluentStorageContainer storage,
        FileSubjectFactory subjectFactory,
        ConfigurableSubjectSerializer serializer,
        TimeProvider timeProvider,
        ILogger? logger)
    {
        _client = new TimeLimitedBlobStorage(client, timeProvider, logger);
        StorageDirectory = storageDirectory;
        TimeProvider = timeProvider;
        Index = new StorageIndex();
        Reconciler = new StorageReconciler(_client, storage, subjectFactory, serializer, Index, timeProvider, logger, _endedSource.Token);
        Worker = new StorageWorker(logger);
        _logger = logger;
    }

    /// <summary>The storage. Every call on it returns within <see cref="StorageCallTimeout.Limit"/>.</summary>
    public IBlobStorage Client => _client;

    /// <inheritdoc cref="TimeLimitedBlobStorage.IsUnresponsive"/>
    public bool IsStorageUnresponsive => _client.IsUnresponsive;

    /// <summary>The directory of a storage on disk, null for any other storage.</summary>
    public string? StorageDirectory { get; }

    /// <summary>The clock for the timeouts and timers of the connection.</summary>
    public TimeProvider TimeProvider { get; }

    /// <remarks>Only read and written on <see cref="Worker"/>, as is the tree that <see cref="Reconciler"/> assigns.</remarks>
    public StorageIndex Index { get; }

    public StorageReconciler Reconciler { get; }

    public StorageWorker Worker { get; }

    /// <summary>Cancelled when the connection ends.</summary>
    public CancellationToken Token => _endedSource.Token;

    public bool IsEnded => _endedSource.IsCancellationRequested;

    /// <summary>
    /// Starts watching <see cref="StorageDirectory"/>, unless the connection has ended.
    /// </summary>
    public void StartFileWatching(Func<FileSystemEventArgs, Task> onFileEvent, Func<Task> onRescanRequired)
    {
        // Checked under the lock that ending takes, so no watcher starts after the connection ended.
        lock (_fileWatcherLock)
        {
            if (IsEnded)
            {
                return;
            }

            _fileWatcher = new StorageFileWatcher(StorageDirectory!, onFileEvent, onRescanRequired, _logger);
            _fileWatcher.Start();
        }
    }

    /// <summary>
    /// Ends the connection: cancels <see cref="Token"/>, stops the watcher and lets the worker take no more
    /// items. The item that is running ends at its next storage call or check.
    /// </summary>
    public void End()
    {
        _endedSource.Cancel();

        lock (_fileWatcherLock)
        {
            _fileWatcher?.Dispose();
            _fileWatcher = null;
        }

        Worker.Dispose();
    }

    /// <summary>
    /// Ends the connection and completes when its worker has finished the item it was running.
    /// </summary>
    /// <param name="cancellationToken">
    /// Cancels the wait for the worker, not the ending. The client is then left to another call of this method
    /// or of <see cref="Dispose"/>.
    /// </param>
    public async Task EndAsync(CancellationToken cancellationToken)
    {
        End();
        await Worker.StopAsync().WaitAsync(cancellationToken);
        Client.Dispose();
    }

    /// <summary>
    /// Ends the connection without waiting for the item its worker is running.
    /// </summary>
    public void Dispose()
    {
        End();
        Client.Dispose();
    }
}
