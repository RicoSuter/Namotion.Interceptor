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
    private readonly TimeLimitedBlobStorage _client;
    // Null for a storage that is not on disk.
    private readonly string? _storageDirectory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger? _logger;

    private volatile ReconcileTrigger? _trigger;
    private volatile StorageFileWatcher? _fileWatcher;
    private bool _hasFileWatcherFailedToStart;

    // True from a pass that this storage refused until the pass that it is owed has been asked for. Only such
    // a pass is asked for when the storage answers again: one that reached the storage keeps to its retries.
    private bool _isPassOwed;

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
        _client = new TimeLimitedBlobStorage(
            client,
            timeProvider,
            logger,
            storageDirectory is null ? null : () => VerifyDirectory(storageDirectory),
            RequestOwedPass);
        Settings = settings;
        _storageDirectory = storageDirectory;
        _timeProvider = timeProvider;
        Index = new StorageIndex();
        Reconciler = new StorageReconciler(_client, storage, subjectFactory, serializer, Index, timeProvider, logger, _endedSource.Token);
        // On the worker, which is the only one that reads the index, and after its last item: nothing places
        // the subjects of an ended connection any more.
        Worker = new StorageWorker(logger, Reconciler.ReleaseUnplacedSubjects, _endedSource.Token);
        _logger = logger;
    }

    public StorageConnectionSettings Settings { get; }

    /// <summary>The storage. Every call on it returns within <see cref="StorageCallTimeout.Limit"/>.</summary>
    public IBlobStorage Client => _client;

    /// <inheritdoc cref="TimeLimitedBlobStorage.IsUnresponsive"/>
    public bool IsStorageUnresponsive => _client.IsUnresponsive;

    /// <remarks>Only read and written on <see cref="Worker"/>, as is the tree that <see cref="Reconciler"/> assigns.</remarks>
    public StorageIndex Index { get; }

    public StorageReconciler Reconciler { get; }

    public StorageWorker Worker { get; }

    public CancellationToken Token => _endedSource.Token;

    public bool IsEnded => _endedSource.IsCancellationRequested;

    /// <summary>
    /// Starts requesting passes: periodically, after a pass that did not finish, and after changes that the
    /// file watcher reports once <see cref="TryStartFileWatcher"/> has started it. Called once, in the step
    /// that makes the connection the current one, and so never after <see cref="StopTrigger"/>.
    /// </summary>
    /// <param name="runPass">Runs a pass of this connection, see <see cref="ReconcileTrigger"/>.</param>
    public void StartTrigger(Func<IReadOnlySet<string>, bool, Task<ReconcileOutcome>> runPass)
    {
        var trigger = new ReconcileTrigger(runPass, TimeSpan.FromSeconds(Settings.ReconcileIntervalSeconds), _timeProvider, _logger);
        _trigger = trigger;

        if (Settings.EnableFileWatching && _storageDirectory != null)
        {
            _fileWatcher = new StorageFileWatcher(_storageDirectory, trigger.NotifyChanged, trigger.NotifyEventsLost, _logger);
        }
    }

    /// <summary>
    /// Starts the file watcher when the storage is watched and the watcher is not running: it was not started
    /// yet, it could not start, or it failed. A watcher that cannot start is logged and tried again by the
    /// next call. The storage is followed by the other passes until then.
    /// </summary>
    /// <remarks>
    /// Starting a watcher can stall on a storage that does not answer, so this is called neither on the worker
    /// nor while a lock is held.
    /// </remarks>
    /// <returns>True when the watcher was started. Changes since it stopped have raised no event.</returns>
    public bool TryStartFileWatcher()
    {
        if (_fileWatcher is not { IsRunning: false } fileWatcher)
        {
            return false;
        }

        try
        {
            // Does nothing for a watcher that StopTrigger has disposed meanwhile.
            fileWatcher.Start();
            if (!fileWatcher.IsRunning)
            {
                return false;
            }

            Volatile.Write(ref _hasFileWatcherFailedToStart, false);
            return true;
        }
        catch (Exception exception)
        {
            // A warning for the first failure since the watcher last ran: every pass tries again.
            var level = Interlocked.Exchange(ref _hasFileWatcherFailedToStart, true) ? LogLevel.Debug : LogLevel.Warning;
            _logger?.Log(level, exception, "File watching cannot start for: {Path}. It is tried again with every pass.", _storageDirectory);
            return false;
        }
    }

    /// <summary>
    /// Tells how a pass that failed with the exception is tried again: by the trigger, or, when this storage
    /// refused a call of the pass, once the storage answers again. Such a pass is then asked for.
    /// </summary>
    public ReconcileOutcome GetOutcomeOfFailedPass(Exception exception)
    {
        if (exception is not StorageUnresponsiveException { IsRefusal: true } refusal || !ReferenceEquals(refusal.Storage, _client))
        {
            return ReconcileOutcome.Failed;
        }

        Volatile.Write(ref _isPassOwed, true);

        // The storage can have answered since it refused the call, and does not tell a second time.
        if (!_client.IsUnresponsive)
        {
            RequestOwedPass();
        }

        return ReconcileOutcome.Refused;
    }

    private void RequestOwedPass()
    {
        if (Interlocked.Exchange(ref _isPassOwed, false))
        {
            _trigger?.NotifyStorageAnswersAgain();
        }
    }

    /// <summary>
    /// Handles a failure as if the file watcher that is running had raised it.
    /// </summary>
    internal void SimulateFileWatcherError(Exception exception)
        => _fileWatcher?.SimulateError(exception);

    /// <inheritdoc cref="ReconcileTrigger.NotifyPassFailed"/>
    public void NotifyPassFailed()
        => _trigger?.NotifyPassFailed();

    /// <summary>
    /// Stops the watcher and the trigger for good, so that nothing requests a pass of this connection any
    /// more. Everything else of the connection stays as it is.
    /// </summary>
    public void StopTrigger()
    {
        Interlocked.Exchange(ref _fileWatcher, null)?.Dispose();
        Interlocked.Exchange(ref _trigger, null)?.Dispose();
    }

    /// <summary>
    /// Ends the connection: cancels <see cref="Token"/>, stops the watcher and the trigger and lets the worker
    /// take no more items. The item that is running ends at its next storage call or check, and the items
    /// that wait behind it are cancelled.
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

    // The disk client lists a directory that is missing or cannot be reached as empty, and a pass would take
    // that for a storage whose files are all gone. It also creates a missing directory again when it writes
    // or is asked about a path in a folder, and the next pass would then find a directory that is there.
    private static void VerifyDirectory(string directory)
    {
        // Throws for a path that cannot be examined, where Directory.Exists only answers false.
        if (!File.GetAttributes(directory).HasFlag(FileAttributes.Directory))
        {
            throw new DirectoryNotFoundException($"The storage directory '{directory}' is not a directory.");
        }
    }
}
