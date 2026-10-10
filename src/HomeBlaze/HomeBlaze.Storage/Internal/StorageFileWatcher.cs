using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Manages FileSystemWatcher with per-path debouncing, event coalescing, and self-write tracking.
/// Coalesces the events of one path (e.g. from editors saving through temp files) into its final state.
/// </summary>
internal sealed class StorageFileWatcher : IDisposable
{
    private static readonly TimeSpan WriteGracePeriod = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CoalesceWindow = TimeSpan.FromMilliseconds(500);

    private readonly string _basePath;
    private readonly Func<FileSystemEventArgs, Task> _onFileEvent;
    private readonly Func<Task> _onRescanRequired;
    private readonly ILogger? _logger;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _pendingWrites = new();
    private readonly FileEventCoalescer _coalescer;

    private FileSystemWatcher? _watcher;

    public StorageFileWatcher(
        string basePath,
        Func<FileSystemEventArgs, Task> onFileEvent,
        Func<Task> onRescanRequired,
        ILogger? logger = null,
        TimeProvider? timeProvider = null)
    {
        _basePath = Path.GetFullPath(basePath);
        _onFileEvent = onFileEvent;
        _onRescanRequired = onRescanRequired;
        _logger = logger;

        // Note: We don't filter temp files before coalescing, a rename from or to one says what happened to the real file
        _coalescer = new FileEventCoalescer(CoalesceWindow, ProcessBatch, timeProvider);
    }

    public void Start()
    {
        _watcher = new FileSystemWatcher(_basePath)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                           NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024, // 64KB buffer to reduce overflow risk
            EnableRaisingEvents = true
        };

        // Route all events to the coalescer: it groups by path and collects events in a time window
        _watcher.Created += OnWatcherEvent;
        _watcher.Changed += OnWatcherEvent;
        _watcher.Deleted += OnWatcherEvent;
        _watcher.Renamed += OnWatcherEvent;
        _watcher.Error += OnWatcherError;

        _logger?.LogInformation("File watching enabled for: {Path}", _basePath);
    }

    private void OnWatcherEvent(object sender, FileSystemEventArgs e)
    {
        if (!IsOwnWrite(e.FullPath))
        {
            _coalescer.Add(e);
        }
    }

    private void ProcessBatch(List<FileSystemEventArgs> events)
    {
        FileSystemEventArgs? coalescedEvent;
        try
        {
            coalescedEvent = CoalesceEvents(events);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error in file event stream");
            return;
        }

        if (coalescedEvent != null)
        {
            _ = ProcessEventSafeAsync(coalescedEvent);
        }
    }

    /// <summary>
    /// Coalesces the events one path received within a window into the single event describing its final state.
    /// Returns null for temp files, which are never tracked.
    /// </summary>
    internal static FileSystemEventArgs? CoalesceEvents(IList<FileSystemEventArgs> events)
    {
        var last = events[^1];
        var rename = events
            .OfType<RenamedEventArgs>()
            .LastOrDefault(e => !StoragePathFilter.IsTemporaryFile(e.OldFullPath));

        if (StoragePathFilter.IsTemporaryFile(last.FullPath))
        {
            // A tracked file renamed to a temp name has left the tree under its old path.
            return rename is not null ? CreateEvent(WatcherChangeTypes.Deleted, rename.OldFullPath) : null;
        }

        // Kept even when later events follow: the handler looks both paths up on disk.
        if (rename is not null)
            return rename;

        if (last.ChangeType == WatcherChangeTypes.Deleted)
            return last;

        // Created must win over a trailing Changed, only a creation makes the handler scan a new directory.
        // A rename from a temp file counts as one, that is how editors save a new file atomically.
        var isNew = events.Any(e => e.ChangeType is WatcherChangeTypes.Created or WatcherChangeTypes.Renamed);
        return isNew ? CreateEvent(WatcherChangeTypes.Created, last.FullPath) : last;
    }

    private static FileSystemEventArgs CreateEvent(WatcherChangeTypes changeType, string fullPath)
        => new(changeType, Path.GetDirectoryName(fullPath)!, Path.GetFileName(fullPath));

    /// <summary>
    /// Marks a path as being written by us (prevents feedback loop).
    /// </summary>
    public void MarkAsOwnWrite(string fullPath)
    {
        _pendingWrites[fullPath] = DateTimeOffset.UtcNow;

        // Schedule cleanup after grace period
        _ = Task.Run(async () =>
        {
            await Task.Delay(WriteGracePeriod);
            _pendingWrites.TryRemove(fullPath, out _);
        });
    }

    /// <summary>
    /// Pushes a file system event into the processing pipeline as if the watcher had raised it.
    /// </summary>
    internal void SimulateFileEvent(FileSystemEventArgs e) => OnWatcherEvent(this, e);

    private bool IsOwnWrite(string fullPath)
    {
        if (_pendingWrites.TryGetValue(fullPath, out var writeTime))
        {
            return DateTimeOffset.UtcNow - writeTime < WriteGracePeriod;
        }
        return false;
    }

    private async Task ProcessEventSafeAsync(FileSystemEventArgs e)
    {
        try
        {
            await _onFileEvent(e);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error processing file event: {Path} ({Type})",
                e.FullPath, e.ChangeType);
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        _logger?.LogError(e.GetException(), "FileSystemWatcher error (buffer overflow?), triggering rescan");
        Restart();
    }

    internal void Restart()
    {
        _watcher?.Dispose();
        Start();

        // Trigger rescan to catch any missed events
        _ = Task.Run(async () =>
        {
            try
            {
                await _onRescanRequired();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to rescan after watcher error");
            }
        });
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
        _coalescer.Dispose();
    }
}
