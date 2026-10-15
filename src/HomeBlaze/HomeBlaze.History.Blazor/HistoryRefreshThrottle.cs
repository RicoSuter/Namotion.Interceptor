namespace HomeBlaze.History.Blazor;

/// <summary>
/// Runs a callback at most once per window: the first <see cref="Arm"/> after the callback ran starts a
/// window that ends with the callback, and further calls before it ends do nothing. Thread-safe.
/// </summary>
/// <remarks>
/// The callback runs on a timer thread while the throttle holds its lock, so it never starts after
/// <see cref="Dispose"/> returned. It must return quickly and must not wait for another thread that arms or
/// disposes the throttle.
/// </remarks>
public sealed class HistoryRefreshThrottle : IDisposable
{
    /// <summary>The shortest window. It also covers the flush interval of a buffering history store.</summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(60);

    private readonly TimeProvider _timeProvider;
    private readonly Action _callback;
    private readonly Lock _lock = new();
    private ITimer? _timer;
    private bool _isDisposed;

    /// <param name="timeProvider">The clock of the windows.</param>
    /// <param name="callback">Runs on a timer thread at the end of a window.</param>
    public HistoryRefreshThrottle(TimeProvider timeProvider, Action callback)
    {
        _timeProvider = timeProvider;
        _callback = callback;
    }

    /// <summary>
    /// Gets the window of a chart: the larger of its refresh interval, at least <see cref="MinimumInterval"/>,
    /// and its bucket. A raw query has no bucket.
    /// </summary>
    public static TimeSpan GetWindow(TimeSpan refreshInterval, TimeSpan? bucket)
    {
        var window = refreshInterval > MinimumInterval ? refreshInterval : MinimumInterval;
        return bucket is { } size && size > window ? size : window;
    }

    /// <summary>Starts a window unless one is running or the throttle is disposed.</summary>
    /// <returns>Whether this call started a window.</returns>
    public bool Arm(TimeSpan window)
    {
        lock (_lock)
        {
            if (_isDisposed || _timer is not null)
            {
                return false;
            }

            // Assigned under the lock that OnElapsed takes first, so an early tick cannot see it unset.
            _timer = _timeProvider.CreateTimer(
                static state => ((HistoryRefreshThrottle)state!).OnElapsed(), this, window, Timeout.InfiniteTimeSpan);
            return true;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void OnElapsed()
    {
        lock (_lock)
        {
            // A tick that was already queued when the throttle was disposed must not run the callback.
            if (_isDisposed)
            {
                return;
            }

            _timer?.Dispose();
            _timer = null;

            // Inside the lock so a concurrent Dispose either wins above or waits for the callback to finish.
            _callback();
        }
    }
}
