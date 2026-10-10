using Microsoft.Extensions.Logging;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Decides when a reconcile pass runs. Changes are collected until the storage has been quiet for
/// <see cref="QuietPeriod"/>, but no longer than <see cref="MaximumDelay"/>. A periodic pass covers changes
/// that no event reported. Only one pass runs at a time, and whatever arrives during it leads to one more.
/// </summary>
internal sealed class ReconcileTrigger : IDisposable
{
    internal static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(1);

    // Without it a folder that is written to more often than once per quiet period would never get a pass.
    internal static readonly TimeSpan MaximumDelay = TimeSpan.FromSeconds(5);

    private readonly Func<IReadOnlySet<string>, bool, Task> _runPass;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger? _logger;
    private readonly ITimer _timer;
    private readonly ITimer? _periodicTimer;
    private readonly Lock _lock = new();

    private HashSet<string> _namedPaths = new(StringComparer.Ordinal);
    private bool _allNamed;
    private bool _isPending;
    private long _firstPendingTimestamp;
    private bool _isRunning;
    private bool _isDisposed;

    /// <param name="runPass">
    /// Runs a pass with the named paths and whether every file counts as named. It is called on the thread of
    /// a timer and must return its task without waiting for the pass. Must not throw.
    /// </param>
    /// <param name="periodicInterval">How often a pass runs without any change. Zero switches it off.</param>
    /// <param name="timeProvider">The clock and timer source, the system one by default.</param>
    /// <param name="logger">Receives a pass that threw despite the contract.</param>
    public ReconcileTrigger(
        Func<IReadOnlySet<string>, bool, Task> runPass,
        TimeSpan periodicInterval,
        TimeProvider? timeProvider = null,
        ILogger? logger = null)
    {
        _runPass = runPass;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;

        // Created within a work item of another storage, the timers would otherwise carry the flow of that item into every pass they start.
        using (ExecutionContext.SuppressFlow())
        {
            _timer = _timeProvider.CreateTimer(
                static state => ((ReconcileTrigger)state!).OnTimer(),
                this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

            if (periodicInterval > TimeSpan.Zero)
            {
                _periodicTimer = _timeProvider.CreateTimer(
                    static state => ((ReconcileTrigger)state!).Request(null, null, allNamed: false, atOnce: true),
                    this, periodicInterval, periodicInterval);
            }
        }
    }

    /// <summary>
    /// Reports that an event named the path, and for a rename the other path as well.
    /// </summary>
    public void NotifyChanged(string path, string? otherPath = null)
        => Request(path, otherPath, allNamed: false, atOnce: false);

    /// <summary>
    /// Reports that events were lost, so the next pass cannot rely on the named paths.
    /// </summary>
    public void NotifyEventsLost()
        => Request(null, null, allNamed: true, atOnce: true);

    private void Request(string? path, string? otherPath, bool allNamed, bool atOnce)
    {
        lock (_lock)
        {
            if (_isDisposed)
                return;

            if (path != null)
            {
                _namedPaths.Add(path);
            }

            if (otherPath != null)
            {
                _namedPaths.Add(otherPath);
            }

            _allNamed |= allNamed;

            if (!_isPending)
            {
                _isPending = true;
                _firstPendingTimestamp = _timeProvider.GetTimestamp();
            }

            // The running pass arms the timer when it ends.
            if (!_isRunning)
            {
                ArmTimer(atOnce);
            }
        }
    }

    private void ArmTimer(bool atOnce)
    {
        var untilMaximum = MaximumDelay - _timeProvider.GetElapsedTime(_firstPendingTimestamp);
        var delay = atOnce || untilMaximum <= TimeSpan.Zero
            ? TimeSpan.Zero
            : untilMaximum < QuietPeriod ? untilMaximum : QuietPeriod;

        _timer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private void OnTimer()
    {
        IReadOnlySet<string> namedPaths;
        bool allNamed;

        lock (_lock)
        {
            if (_isDisposed || _isRunning || !_isPending)
                return;

            namedPaths = _namedPaths;
            allNamed = _allNamed;

            _namedPaths = new HashSet<string>(StringComparer.Ordinal);
            _allNamed = false;
            _isPending = false;
            _isRunning = true;
        }

        _ = RunPassAsync(namedPaths, allNamed);
    }

    private async Task RunPassAsync(IReadOnlySet<string> namedPaths, bool allNamed)
    {
        try
        {
            await _runPass(namedPaths, allNamed);
        }
        catch (Exception exception)
        {
            _logger?.LogError(exception, "Reconcile pass failed");
        }
        finally
        {
            lock (_lock)
            {
                _isRunning = false;
                if (_isPending && !_isDisposed)
                {
                    ArmTimer(atOnce: false);
                }
            }
        }
    }

    /// <summary>
    /// Stops the timers and drops what is pending. A pass that is running is not waited for.
    /// </summary>
    public void Dispose()
    {
        lock (_lock)
        {
            _isDisposed = true;
        }

        _timer.Dispose();
        _periodicTimer?.Dispose();
    }
}
