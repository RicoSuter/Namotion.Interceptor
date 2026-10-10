using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Manages FileSystemWatcher with Rx-based debouncing, event coalescing, and self-write tracking.
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
    private readonly Subject<FileSystemEventArgs> _fileEvents = new();

    private FileSystemWatcher? _watcher;
    private IDisposable? _fileEventSubscription;

    public StorageFileWatcher(
        string basePath,
        Func<FileSystemEventArgs, Task> onFileEvent,
        Func<Task> onRescanRequired,
        ILogger? logger = null)
    {
        _basePath = Path.GetFullPath(basePath);
        _onFileEvent = onFileEvent;
        _onRescanRequired = onRescanRequired;
        _logger = logger;
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

        // Route all events to the Rx subject
        _watcher.Created += OnFileSystemEvent;
        _watcher.Changed += OnFileSystemEvent;
        _watcher.Deleted += OnFileSystemEvent;
        _watcher.Renamed += OnFileSystemEvent;
        _watcher.Error += OnWatcherError;

        // Process events with coalescing: group by path, collect events in time window, then coalesce
        // Note: We don't filter temp files here, a rename from or to one says what happened to the real file
        _fileEventSubscription = _fileEvents
            .Where(e => !IsOwnWrite(e.FullPath))
            .GroupBy(e => GetCanonicalPath(e.FullPath))
            .SelectMany(group => group
                .Buffer(CoalesceWindow)
                .Where(batch => batch.Count > 0)
                .Select(CoalesceEvents))
            .Where(e => e != null)
            .Subscribe(
                onNext: async e => await ProcessEventSafeAsync(e!),
                onError: ex => _logger?.LogError(ex, "Error in file event stream"));

        _logger?.LogInformation("File watching enabled for: {Path}", _basePath);
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
    /// Gets canonical path for grouping (handles case-insensitivity on Windows).
    /// Never a path to act on: lowercased, it does not exist on a case-sensitive file system.
    /// </summary>
    private static string GetCanonicalPath(string fullPath)
        => fullPath.ToLowerInvariant();

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

    internal void OnFileSystemEvent(object? sender, FileSystemEventArgs e)
        => _fileEvents.OnNext(e);

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        _logger?.LogError(e.GetException(), "FileSystemWatcher error (buffer overflow?), triggering rescan");
        Restart();
    }

    internal void Restart()
    {
        // Start subscribes again, a subscription left behind would handle every event once more.
        _fileEventSubscription?.Dispose();
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
        _fileEventSubscription?.Dispose();
        _fileEvents.Dispose();
        _watcher?.Dispose();
        _watcher = null;
    }
}
