namespace HomeBlaze.History.Blazor;

/// <summary>
/// Runs a callback at most once per window: the first <see cref="Arm"/> after the callback ran starts a
/// window that ends with the callback, and further calls before it ends do nothing. <see cref="Restart"/> replaces a
/// running window. Thread-safe.
/// </summary>
/// <remarks>
/// The callback runs on a timer thread while the throttle holds its lock, so it never starts after
/// <see cref="Dispose"/> returned. It must return quickly. Posting to a dispatcher, for example with
/// <c>InvokeAsync</c>, is fine even when the dispatcher runs the work inline on the timer thread, but the callback
/// must never block waiting for the dispatcher or for another thread that arms or disposes the throttle.
/// </remarks>
internal sealed class HistoryRefreshThrottle : IDisposable
{
    /// <summary>The shortest window. It also covers the flush interval of a buffering history store.</summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(60);

    /// <summary>The longest window. A timer cannot wait much longer than 49 days.</summary>
    public static readonly TimeSpan MaximumInterval = TimeSpan.FromDays(1);

    private readonly TimeProvider _timeProvider;
    private readonly Action _callback;
    private readonly Lock _lock = new();
    private RunningWindow? _window;
    private bool _isDisposed;

    /// <param name="timeProvider">The clock of the windows.</param>
    /// <param name="callback">Runs on a timer thread at the end of a window.</param>
    public HistoryRefreshThrottle(TimeProvider timeProvider, Action callback)
    {
        _timeProvider = timeProvider;
        _callback = callback;
    }

    /// <summary>
    /// Gets the window of a chart: the larger of its refresh interval and its bucket, from <see cref="MinimumInterval"/>
    /// to <see cref="MaximumInterval"/>. A raw query has no bucket.
    /// </summary>
    public static TimeSpan GetWindow(TimeSpan refreshInterval, TimeSpan? bucket)
    {
        var window = bucket is { } size && size > refreshInterval ? size : refreshInterval;
        return window < MinimumInterval ? MinimumInterval : window > MaximumInterval ? MaximumInterval : window;
    }

    /// <summary>Gets whether a window is running.</summary>
    public bool IsArmed
    {
        get
        {
            lock (_lock)
            {
                return _window is not null;
            }
        }
    }

    /// <summary>Starts a window unless one is running or the throttle is disposed.</summary>
    /// <param name="window">The length of the window. Must be positive.</param>
    /// <returns>Whether this call started a window.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="window"/> is zero or negative.</exception>
    public bool Arm(TimeSpan window) => Start(window, restart: false);

    /// <summary>Starts a window that ends after <paramref name="window"/>, in place of a running one, unless the throttle is disposed.</summary>
    /// <param name="window">The length of the window. Must be positive.</param>
    /// <returns>Whether this call started a window.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="window"/> is zero or negative.</exception>
    public bool Restart(TimeSpan window) => Start(window, restart: true);

    private bool Start(TimeSpan window, bool restart)
    {
        // A zero due time fires inside CreateTimer, before _window is assigned, which would leave the throttle armed forever.
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);

        lock (_lock)
        {
            if (_isDisposed || (_window is not null && !restart))
            {
                return false;
            }

            // A tick of the replaced timer that is already queued finds another window in OnElapsed and does nothing.
            _window?.Timer?.Dispose();

            // The caller's execution context, for example one of a render that holds the read recorder of a tracking
            // scope, must not flow into the callback.
            var isFlowSuppressed = ExecutionContext.IsFlowSuppressed();
            var flowControl = isFlowSuppressed ? default : ExecutionContext.SuppressFlow();
            try
            {
                // Assigned under the lock that OnElapsed takes first, so an early tick cannot see it unset.
                var runningWindow = new RunningWindow(this);
                runningWindow.Timer = _timeProvider.CreateTimer(
                    static state => ((RunningWindow)state!).Owner.OnElapsed((RunningWindow)state), runningWindow, window, Timeout.InfiniteTimeSpan);
                _window = runningWindow;
            }
            finally
            {
                if (!isFlowSuppressed)
                {
                    flowControl.Undo();
                }
            }

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
            _window?.Timer?.Dispose();
            _window = null;
        }
    }

    private void OnElapsed(RunningWindow window)
    {
        lock (_lock)
        {
            // A tick that was already queued when the throttle was disposed or the window restarted must not run the callback.
            if (_isDisposed || window != _window)
            {
                return;
            }

            window.Timer?.Dispose();
            _window = null;

            // Inside the lock so a concurrent Dispose either wins above or waits for the callback to finish.
            _callback();
        }
    }

    // The state of a timer, so a tick of a replaced timer can be told apart from one of the running window.
    private sealed class RunningWindow(HistoryRefreshThrottle owner)
    {
        public HistoryRefreshThrottle Owner { get; } = owner;

        public ITimer? Timer { get; set; }
    }
}
