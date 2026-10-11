using Microsoft.Extensions.Logging;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Decides when a reconcile pass runs. Changes are collected until the storage has been quiet for
/// <see cref="QuietPeriod"/>, but no longer than <see cref="MaximumDelay"/>. A periodic pass covers changes
/// that no event reported. A pass that did not finish is followed by another one after
/// <see cref="MaximumDelay"/>, at most <see cref="MaximumRetryCount"/> times in a row. Only one pass runs at a
/// time, and whatever arrives during it leads to one more.
/// </summary>
internal sealed class ReconcileTrigger : IDisposable
{
    internal static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(1);

    // Without it a folder that is written to more often than once per quiet period would never get a pass.
    internal static readonly TimeSpan MaximumDelay = TimeSpan.FromSeconds(5);

    internal const int MaximumRetryCount = 3;

    // The longest period a timer accepts.
    private static readonly TimeSpan MaximumPeriodicInterval = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    private readonly Func<IReadOnlySet<string>, bool, Task<bool>> _runPass;
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

    // The retries asked for since a pass last finished.
    private int _retryCount;

    /// <param name="runPass">
    /// Runs a pass with the named paths and whether every file counts as named, and returns whether the pass
    /// finished. False asks for another pass. It is called on the thread of a timer and must return its task
    /// without waiting for the pass. Must not throw.
    /// </param>
    /// <param name="periodicInterval">
    /// How often a pass runs without any change. Zero or less switches it off, and an interval longer than a
    /// timer accepts (about 49 days) is shortened to that.
    /// </param>
    /// <param name="timeProvider">The clock and timer source, the system one by default.</param>
    /// <param name="logger">Receives a pass that threw despite the contract.</param>
    public ReconcileTrigger(
        Func<IReadOnlySet<string>, bool, Task<bool>> runPass,
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
                if (periodicInterval > MaximumPeriodicInterval)
                {
                    periodicInterval = MaximumPeriodicInterval;
                }

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

    /// <summary>
    /// Reports that a pass which this trigger did not start has not finished, so that it is retried like one
    /// of its own.
    /// </summary>
    public void NotifyPassFailed()
    {
        lock (_lock)
        {
            // A pass that runs or waits for the worker is the retry, and reports its own outcome.
            if (!_isDisposed && !_isRunning)
            {
                ArmTimerAfterPass(isFinished: false);
            }
        }
    }

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
                ArmTimer(atOnce ? TimeSpan.Zero : QuietPeriod);
            }
        }
    }

    private void ArmTimer(TimeSpan delay)
    {
        var untilMaximum = MaximumDelay - _timeProvider.GetElapsedTime(_firstPendingTimestamp);
        if (untilMaximum < delay)
        {
            delay = untilMaximum;
        }

        _timer.Change(delay > TimeSpan.Zero ? delay : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    private void ArmTimerAfterPass(bool isFinished)
    {
        var delay = QuietPeriod;
        if (isFinished)
        {
            _retryCount = 0;
        }
        else if (_retryCount < MaximumRetryCount)
        {
            _retryCount++;

            // With a change pending, the pass for that change is the retry.
            if (!_isPending)
            {
                _isPending = true;
                _firstPendingTimestamp = _timeProvider.GetTimestamp();
                delay = MaximumDelay;
            }
        }

        if (_isPending)
        {
            ArmTimer(delay);
        }
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
        var isFinished = false;
        try
        {
            isFinished = await _runPass(namedPaths, allNamed);
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
                if (!_isDisposed)
                {
                    ArmTimerAfterPass(isFinished);
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
