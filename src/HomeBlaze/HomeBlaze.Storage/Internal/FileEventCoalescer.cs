namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Collects the file system events of one path (compared case-insensitively) that arrive within one window
/// and hands them over as a single batch when the window closes. The first event of an idle path opens its
/// window, and a path holds no state once its batch has been handed over.
/// </summary>
internal sealed class FileEventCoalescer : IDisposable
{
    private static readonly TimeSpan MinimumTimerDelay = TimeSpan.FromMilliseconds(1);

    private readonly TimeSpan _window;
    private readonly TimeProvider _timeProvider;
    private readonly Action<List<FileSystemEventArgs>> _onBatch;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, List<FileSystemEventArgs>> _eventsByPath = new(StringComparer.OrdinalIgnoreCase);

    // Every window has the same length, so batches close in the order they were opened
    // and one timer aimed at the oldest batch serves all paths.
    private readonly Queue<PendingBatch> _pendingBatches = new();
    private readonly ITimer _timer;

    private bool _isDraining;
    private bool _isDisposed;

    /// <param name="window">How long events of one path are collected after its first event.</param>
    /// <param name="onBatch">
    /// Receives the events of one path in arrival order. Called for one batch at a time and must not throw.
    /// </param>
    /// <param name="timeProvider">The clock and timer source, the system one by default.</param>
    public FileEventCoalescer(
        TimeSpan window,
        Action<List<FileSystemEventArgs>> onBatch,
        TimeProvider? timeProvider = null)
    {
        _window = window;
        _onBatch = onBatch;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _timer = _timeProvider.CreateTimer(
            static state => ((FileEventCoalescer)state!).Drain(),
            this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// The number of paths with an open window.
    /// </summary>
    internal int PendingPathCount
    {
        get
        {
            lock (_lock)
            {
                return _eventsByPath.Count;
            }
        }
    }

    /// <summary>
    /// Adds an event to the open window of its path, or opens one. Does nothing once disposed.
    /// </summary>
    public void Add(FileSystemEventArgs fileEvent)
    {
        var path = fileEvent.FullPath;

        lock (_lock)
        {
            if (_isDisposed)
                return;

            if (_eventsByPath.TryGetValue(path, out var events))
            {
                events.Add(fileEvent);
                return;
            }

            events = [fileEvent];
            _eventsByPath.Add(path, events);
            _pendingBatches.Enqueue(new PendingBatch(path, events, _timeProvider.GetTimestamp()));

            // A running drain arms the timer itself before it returns, arming it here as well
            // could start a second drain that overtakes the first.
            if (_pendingBatches.Count == 1 && !_isDraining)
            {
                _timer.Change(_window, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void Drain()
    {
        lock (_lock)
        {
            _isDraining = true;
        }

        while (true)
        {
            PendingBatch batch;

            lock (_lock)
            {
                if (_isDisposed || _pendingBatches.Count == 0)
                {
                    _isDraining = false;
                    return;
                }

                batch = _pendingBatches.Peek();

                var remaining = _window - _timeProvider.GetElapsedTime(batch.OpenedTimestamp);
                if (remaining > TimeSpan.Zero)
                {
                    // The floor keeps a remainder below the timer resolution from re-firing in a tight loop.
                    _isDraining = false;
                    _timer.Change(
                        remaining < MinimumTimerDelay ? MinimumTimerDelay : remaining,
                        Timeout.InfiniteTimeSpan);
                    return;
                }

                _pendingBatches.Dequeue();
                _eventsByPath.Remove(batch.Path);
            }

            // Outside the lock: the callback runs handler code, and events keep arriving meanwhile.
            _onBatch(batch.Events);
        }
    }

    /// <summary>
    /// Drops the open windows without handing them over.
    /// </summary>
    public void Dispose()
    {
        lock (_lock)
        {
            _isDisposed = true;
            _eventsByPath.Clear();
            _pendingBatches.Clear();
        }

        _timer.Dispose();
    }

    private readonly record struct PendingBatch(string Path, List<FileSystemEventArgs> Events, long OpenedTimestamp);
}
