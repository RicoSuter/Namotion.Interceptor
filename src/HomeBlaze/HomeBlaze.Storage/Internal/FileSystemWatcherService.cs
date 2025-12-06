using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Manages FileSystemWatcher with Rx-based debouncing and self-write tracking.
/// </summary>
internal sealed class FileSystemWatcherService : IDisposable
{
    private static readonly TimeSpan WriteGracePeriod = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromSeconds(1);

    private readonly string _basePath;
    private readonly Func<FileSystemEventArgs, Task> _onFileEvent;
    private readonly Func<Task> _onRescanRequired;
    private readonly ILogger? _logger;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _pendingWrites = new();
    private readonly Subject<FileSystemEventArgs> _fileEvents = new();

    private FileSystemWatcher? _watcher;
    private IDisposable? _fileEventSubscription;

    public FileSystemWatcherService(
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
        _watcher.Created += (s, e) => _fileEvents.OnNext(e);
        _watcher.Changed += (s, e) => _fileEvents.OnNext(e);
        _watcher.Deleted += (s, e) => _fileEvents.OnNext(e);
        _watcher.Renamed += (s, e) => _fileEvents.OnNext(e);
        _watcher.Error += OnWatcherError;

        // Process events with proper debouncing using System.Reactive
        _fileEventSubscription = _fileEvents
            .GroupBy(e => e.FullPath)
            .SelectMany(group => group.Throttle(DebounceInterval))
            .Where(e => !IsOwnWrite(e.FullPath))
            .Subscribe(
                onNext: async e => await ProcessEventSafeAsync(e),
                onError: ex => _logger?.LogError(ex, "Error in file event stream"));

        _logger?.LogInformation("File watching enabled for: {Path}", _basePath);
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
