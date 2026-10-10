using Microsoft.Extensions.Logging;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Reports which paths of a directory the file system changed. It does not interpret the events:
/// what happened to a path is decided by looking at the storage.
/// </summary>
internal sealed class StorageFileWatcher : IDisposable
{
    private readonly string _basePath;
    private readonly Action<string, string?> _onChanged;
    private readonly Action _onEventsLost;
    private readonly ILogger? _logger;

    private FileSystemWatcher? _watcher;
    private volatile bool _isDisposed;

    /// <param name="basePath">The watched directory.</param>
    /// <param name="onChanged">Receives the path within the directory, and for a rename the old path as well.</param>
    /// <param name="onEventsLost">Called when the watcher failed and events may be missing.</param>
    /// <param name="logger">The logger.</param>
    public StorageFileWatcher(
        string basePath,
        Action<string, string?> onChanged,
        Action onEventsLost,
        ILogger? logger = null)
    {
        _basePath = Path.GetFullPath(basePath);
        _onChanged = onChanged;
        _onEventsLost = onEventsLost;
        _logger = logger;
    }

    /// <summary>
    /// Starts watching, or starts over when it already does. Does nothing once disposed.
    /// </summary>
    public void Start()
    {
        if (_isDisposed)
            return;

        Interlocked.Exchange(ref _watcher, null)?.Dispose();

        FileSystemWatcher watcher;

        // Started within a work item of another storage, the watcher would otherwise carry the flow of that item into every event it raises.
        using (ExecutionContext.SuppressFlow())
        {
            watcher = new FileSystemWatcher(_basePath)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                               NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024, // 64KB buffer to reduce overflow risk
                EnableRaisingEvents = true
            };
        }

        watcher.Created += OnWatcherEvent;
        watcher.Changed += OnWatcherEvent;
        watcher.Deleted += OnWatcherEvent;
        watcher.Renamed += OnWatcherEvent;
        watcher.Error += OnWatcherError;

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
    internal void SimulateFileEvent(FileSystemEventArgs e) => OnWatcherEvent(this, e);

    /// <summary>
    /// Handles a failure as if the watcher had raised it.
    /// </summary>
    internal void SimulateError(Exception exception) => OnWatcherError(this, new ErrorEventArgs(exception));

    private void OnWatcherEvent(object sender, FileSystemEventArgs e)
    {
        var path = GetRelativePath(e.FullPath);
        var oldPath = e is RenamedEventArgs { OldFullPath: { } oldFullPath } ? GetRelativePath(oldFullPath) : null;

        // A rename between a tracked name and an ignored one changes the tree, so it counts when either side is tracked.
        if (StoragePathFilter.IsIgnored(path) && (oldPath == null || StoragePathFilter.IsIgnored(oldPath)))
            return;

        _onChanged(path, oldPath);
    }

    private string GetRelativePath(string fullPath)
        => Path.GetRelativePath(_basePath, fullPath).Replace('\\', '/');

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        _logger?.LogError(e.GetException(), "FileSystemWatcher error (buffer overflow?), events may be lost");

        try
        {
            Start();
        }
        catch (Exception exception)
        {
            // The periodic pass still follows the storage without a watcher.
            _logger?.LogError(exception, "Failed to restart the file watcher for: {Path}", _basePath);
        }

        _onEventsLost();
    }

    public void Dispose()
    {
        _isDisposed = true;
        Interlocked.Exchange(ref _watcher, null)?.Dispose();
    }
}
