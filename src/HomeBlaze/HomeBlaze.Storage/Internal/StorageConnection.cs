using FluentStorage.Blobs;
using HomeBlaze.Services;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// The settings of a container that decide which storage it connects to and how it follows it.
/// </summary>
internal readonly record struct StorageConnectionSettings(
    string StorageType,
    string ConnectionString,
    string? ContainerName,
    bool EnableFileWatching,
    int ReconcileIntervalSeconds)
{
    public bool IsInMemory => StorageType == "inmemory";
}

/// <summary>
/// Everything that belongs to one connection of a container to its storage. A reconnect replaces it as a whole,
/// so work of an ended connection never touches the index or the client of the connection after it.
/// </summary>
internal sealed class StorageConnection : IDisposable
{
    private readonly CancellationTokenSource _endedSource = new();
    private readonly Lock _triggerLock = new();
    private readonly TimeLimitedBlobStorage _client;
    private readonly ILogger? _logger;

    private ReconcileTrigger? _trigger;
    private StorageFileWatcher? _fileWatcher;
    private bool _isTriggerStopped;

    public StorageConnection(
        StorageConnectionSettings settings,
        IBlobStorage client,
        string? storageDirectory,
        FluentStorageContainer storage,
        FileSubjectFactory subjectFactory,
        ConfigurableSubjectSerializer serializer,
        TimeProvider timeProvider,
        ILogger? logger)
    {
        _client = new TimeLimitedBlobStorage(client, timeProvider, logger);
        Settings = settings;
        StorageDirectory = storageDirectory;
        TimeProvider = timeProvider;
        Index = new StorageIndex();
        Reconciler = new StorageReconciler(_client, storage, subjectFactory, serializer, Index, timeProvider, logger, _endedSource.Token);
        Worker = new StorageWorker(logger);
        _logger = logger;
    }

    /// <summary>The settings the connection was created with.</summary>
    public StorageConnectionSettings Settings { get; }

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
    /// Starts requesting passes: after changes that the file watcher reports, and periodically. Does nothing
    /// once <see cref="StopTrigger"/> was called or the connection has ended.
    /// </summary>
    /// <param name="runPass">Runs a pass of this connection, see <see cref="ReconcileTrigger"/>.</param>
    /// <param name="periodicInterval">How often a pass runs without any change. Zero or less switches it off.</param>
    /// <param name="watchFiles">Whether <see cref="StorageDirectory"/> is watched.</param>
    public void StartTrigger(Func<IReadOnlySet<string>, bool, Task> runPass, TimeSpan periodicInterval, bool watchFiles)
    {
        // Checked under the lock that stopping takes, so nothing starts after the trigger was stopped.
        lock (_triggerLock)
        {
            if (_isTriggerStopped)
            {
                return;
            }

            var trigger = new ReconcileTrigger(runPass, periodicInterval, TimeProvider, _logger);

            if (watchFiles)
            {
                var fileWatcher = new StorageFileWatcher(StorageDirectory!, trigger.NotifyChanged, trigger.NotifyEventsLost, _logger);
                try
                {
                    fileWatcher.Start();
                }
                catch
                {
                    trigger.Dispose();
                    throw;
                }

                _fileWatcher = fileWatcher;
            }

            _trigger = trigger;
        }
    }

    /// <summary>
    /// Stops the watcher and the trigger for good, so that nothing requests a pass of this connection any
    /// more. Everything else of the connection stays as it is.
    /// </summary>
    public void StopTrigger()
    {
        lock (_triggerLock)
        {
            _isTriggerStopped = true;

            _fileWatcher?.Dispose();
            _fileWatcher = null;
            _trigger?.Dispose();
            _trigger = null;
        }
    }

    /// <summary>
    /// Ends the connection: cancels <see cref="Token"/>, stops the watcher and the trigger and lets the worker
    /// take no more items. The item that is running ends at its next storage call or check.
    /// </summary>
    public void End()
    {
        _endedSource.Cancel();
        StopTrigger();
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
