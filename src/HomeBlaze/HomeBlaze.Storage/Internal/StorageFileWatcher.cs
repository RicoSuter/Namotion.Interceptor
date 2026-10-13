using Microsoft.Extensions.Logging;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Reports which paths of a directory the file system changed. It does not interpret the events:
/// what happened to a path is decided by looking at the storage.
/// </summary>
internal sealed class StorageFileWatcher : IDisposable
{
    private readonly string _basePath;
    private readonly Action<string> _onChanged;
    private readonly Action _onEventsLost;
    private readonly ILogger? _logger;

    private FileSystemWatcher? _watcher;
    private volatile bool _isDisposed;

    /// <param name="basePath">The watched directory.</param>
    /// <param name="onChanged">Receives each path within the directory that an event names and that is not ignored. A rename names two.</param>
    /// <param name="onEventsLost">Called when the watcher failed and events may be missing.</param>
    /// <param name="logger">The logger.</param>
    public StorageFileWatcher(
        string basePath,
        Action<string> onChanged,
        Action onEventsLost,
        ILogger? logger = null)
    {
        _basePath = Path.GetFullPath(basePath);
        _onChanged = onChanged;
        _onEventsLost = onEventsLost;
        _logger = logger;
    }

    /// <summary>
    /// False before the first start, after a start that failed, and after an error that starting over at once
    /// does not help. <see cref="Start"/> then starts it again.
    /// </summary>
    public bool IsRunning => Volatile.Read(ref _watcher) != null;

    /// <summary>
    /// Starts watching, or starts over when it already does. Does nothing once disposed.
    /// </summary>
    public void Start()
    {
        if (_isDisposed)
            return;

        Interlocked.Exchange(ref _watcher, null)?.Dispose();

        var watcher = new FileSystemWatcher(_basePath)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                           NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024 // 64KB buffer to reduce overflow risk
        };

        watcher.Created += OnWatcherEvent;
        watcher.Changed += OnWatcherEvent;
        watcher.Deleted += OnWatcherEvent;
        watcher.Renamed += OnWatcherEvent;
        watcher.Error += OnWatcherError;

        try
        {
            // Started within a work item of another storage, the watcher would otherwise carry the flow of that item into every event it raises.
            using (ExecutionContext.SuppressFlow())
            {
                watcher.EnableRaisingEvents = true;
            }
        }
        catch
        {
            watcher.Dispose();
            throw;
        }

        Interlocked.Exchange(ref _watcher, watcher)?.Dispose();

        // A failed watcher starts over on its own thread. A Dispose since the check above did not see this one.
        if (_isDisposed)
        {
            Interlocked.Exchange(ref _watcher, null)?.Dispose();
            return;
        }

        _logger?.LogInformation("File watching enabled for: {Path}", _basePath);
    }

    /// <summary>
    /// Handles a file system event as if the watcher had raised it.
    /// </summary>
    internal void SimulateFileEvent(FileSystemEventArgs fileEvent) => OnWatcherEvent(this, fileEvent);

    /// <summary>
    /// Handles a failure as if a watcher had raised it: the one that is running, or one that is not running
    /// any more or not yet.
    /// </summary>
    internal void SimulateError(Exception exception, bool isRaisedByRunningWatcher = true)
        => OnWatcherError(isRaisedByRunningWatcher ? Volatile.Read(ref _watcher) : null, new ErrorEventArgs(exception));

    private void OnWatcherEvent(object sender, FileSystemEventArgs fileEvent)
    {
        Report(fileEvent.FullPath);
        if (fileEvent is RenamedEventArgs { OldFullPath: { } oldFullPath })
        {
            Report(oldFullPath);
        }
    }

    private void Report(string fullPath)
    {
        var path = Path.GetRelativePath(_basePath, fullPath).Replace('\\', '/');
        if (!StoragePathFilter.IsIgnored(path))
        {
            _onChanged(path);
        }
    }

    private void OnWatcherError(object? sender, ErrorEventArgs error)
    {
        var failure = error.GetException();
        _logger?.LogError(failure, "File watcher error, events may be lost for: {Path}", _basePath);

        // Only for the watcher that is running. A watcher can raise an error from within its own start, and
        // starting over from there would start again within that start, once more for every level.
        if (sender is FileSystemWatcher watcher && ReferenceEquals(watcher, Volatile.Read(ref _watcher)))
        {
            if (failure is InternalBufferOverflowException)
            {
                try
                {
                    Start();
                }
                catch (Exception exception)
                {
                    _logger?.LogError(exception, "Failed to restart the file watcher for: {Path}", _basePath);
                }
            }
            else if (ReferenceEquals(Interlocked.CompareExchange(ref _watcher, null, watcher), watcher))
            {
                // Starting over at once would meet the same error. Not running, it is started again later.
                watcher.Dispose();
            }
        }

        _onEventsLost();
    }

    public void Dispose()
    {
        _isDisposed = true;
        Interlocked.Exchange(ref _watcher, null)?.Dispose();
    }
}
