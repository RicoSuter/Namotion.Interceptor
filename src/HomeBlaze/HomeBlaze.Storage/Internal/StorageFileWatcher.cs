using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Manages FileSystemWatcher with Rx-based debouncing, event coalescing, and self-write tracking.
/// Coalesces delete+create patterns (from editors using temp files) into update events.
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
        _watcher.Created += (_, e) => _fileEvents.OnNext(e);
        _watcher.Changed += (_, e) => _fileEvents.OnNext(e);
        _watcher.Deleted += (_, e) => _fileEvents.OnNext(e);
        _watcher.Renamed += (_, e) => _fileEvents.OnNext(e);
        _watcher.Error += OnWatcherError;

        // Process events with coalescing: group by path, collect events in time window, then coalesce
        // Note: We don't filter temp files here - they're handled in coalescing logic
        _fileEventSubscription = _fileEvents
            .Where(e => !IsOwnWrite(e.FullPath))
            .GroupBy(e => GetCanonicalPath(e.FullPath))
            .SelectMany(group => group
                .Buffer(CoalesceWindow)
                .Where(batch => batch.Count > 0)
                .Select(batch => CoalesceEvents(group.Key, batch)))
            .Where(e => e != null)
            .Subscribe(
                onNext: async e => await ProcessEventSafeAsync(e!),
                onError: ex => _logger?.LogError(ex, "Error in file event stream"));

        _logger?.LogInformation("File watching enabled for: {Path}", _basePath);
    }

    /// <summary>
    /// Coalesces a batch of events for the same path into a single effective event.
    /// Key insight: if a file is deleted and (re)appears in same window = update.
    /// Filters out temp file events at the end.
    /// </summary>
    private FileSystemEventArgs? CoalesceEvents(string canonicalPath, IList<FileSystemEventArgs> events)
    {
        if (events.Count == 0)
            return null;

        // Skip temp files entirely - return null to filter them out
        if (IsTempFile(canonicalPath))
            return null;

        var hasDelete = events.Any(e => e.ChangeType == WatcherChangeTypes.Deleted);
        var hasCreate = events.Any(e => e.ChangeType == WatcherChangeTypes.Created);
        var hasChange = events.Any(e => e.ChangeType == WatcherChangeTypes.Changed);
        var renamed = events.OfType<RenamedEventArgs>().LastOrDefault();

        // Delete + (Create OR Rename-to-this-path) = Update
        // This handles: delete+create, AND delete+rename-from-temp patterns
        if (hasDelete && (hasCreate || renamed != null))
        {
            _logger?.LogDebug("Coalesced delete+reappear to update for: {Path}", canonicalPath);
            return new FileSystemEventArgs(WatcherChangeTypes.Changed,
                Path.GetDirectoryName(canonicalPath) ?? _basePath,
                Path.GetFileName(canonicalPath));
        }

        // Handle genuine rename (not from temp file)
        if (renamed != null && !IsTempFile(renamed.OldFullPath))
            return renamed;

        // Rename from temp file (without prior delete) = effectively a create/update
        if (renamed != null && IsTempFile(renamed.OldFullPath))
        {
            _logger?.LogDebug("Treating rename-from-temp as update for: {Path}", canonicalPath);
            return new FileSystemEventArgs(WatcherChangeTypes.Changed,
                Path.GetDirectoryName(canonicalPath) ?? _basePath,
                Path.GetFileName(canonicalPath));
        }

        // Just delete
        if (hasDelete && !hasCreate)
            return events.First(e => e.ChangeType == WatcherChangeTypes.Deleted);

        // Just create
        if (hasCreate && !hasDelete)
            return events.First(e => e.ChangeType == WatcherChangeTypes.Created);

        // Change events (deduplicate)
        if (hasChange)
            return events.First(e => e.ChangeType == WatcherChangeTypes.Changed);

        // Fallback: return last event
        return events.Last();
    }

    /// <summary>
    /// Gets canonical path for grouping (handles case-insensitivity on Windows).
    /// </summary>
    private string GetCanonicalPath(string fullPath)
        => fullPath.ToLowerInvariant();

    /// <summary>
    /// Checks if path is a temporary file created by editors.
    /// </summary>
    private static bool IsTempFile(string fullPath)
    {
        var fileName = Path.GetFileName(fullPath);
        return fileName.StartsWith("~") ||
               fileName.EndsWith("~") ||
               fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
               fileName.Contains(".tmp.", StringComparison.OrdinalIgnoreCase);
    }

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
    /// Converts a full path to a relative path within the watched directory.
    /// </summary>
    public string GetRelativePath(string fullPath)
        => Path.GetRelativePath(_basePath, fullPath).Replace('\\', '/');

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

    private void Restart()
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
        _fileEventSubscription?.Dispose();
        _fileEvents.Dispose();
        _watcher?.Dispose();
        _watcher = null;
    }
}
