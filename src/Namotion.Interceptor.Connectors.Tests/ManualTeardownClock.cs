namespace Namotion.Interceptor.Connectors.Tests;

/// <summary>
/// A clock for the teardown bound that only moves when a test advances it, so a test can state that
/// stopping ends once the bound has elapsed without racing the wall clock on a loaded machine.
/// </summary>
/// <remarks>
/// Due timers fire on the thread pool, as real timers do, so code that runs when the bound elapses never
/// runs inline on the test thread and cannot block it.
/// <para>
/// A clock that is never advanced keeps the bound from elapsing, so a stop waits for the run to finish,
/// including its teardown flush. A test that asserts what the stop delivers or releases needs one: on the
/// wall clock a stalled runner can let the bound elapse first, and the stop then returns while the run is
/// still finishing. Pair it with a timeout on the awaited stop, so a run that never finishes fails the
/// test instead of hanging the suite.
/// </para>
/// </remarks>
internal sealed class ManualTeardownClock : TimeProvider
{
    private static readonly TimeSpan ArmTimeout = TimeSpan.FromSeconds(30);

    private readonly object _lock = new();
    private readonly List<ManualTimer> _timers = [];
    private TaskCompletionSource _timerArmed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TimeSpan _elapsed;

    /// <summary>
    /// Waits until a timer is armed to fire within <paramref name="delay"/> from now.
    /// </summary>
    /// <exception cref="TimeoutException">No such timer was armed within 30 seconds.</exception>
    public async Task WaitForTimerAsync(TimeSpan delay)
    {
        using var timeout = new CancellationTokenSource(ArmTimeout);
        while (true)
        {
            Task timerArmed;
            lock (_lock)
            {
                var deadline = _elapsed + delay;
                if (_timers.Exists(timer => timer.DueAt <= deadline))
                {
                    return;
                }

                timerArmed = _timerArmed.Task;
            }

            try
            {
                await timerArmed.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                throw new TimeoutException($"No timer was armed to fire within {delay}.");
            }
        }
    }

    /// <summary>
    /// Moves the clock forward by <paramref name="delay"/> and fires every timer that has come due.
    /// </summary>
    public void Advance(TimeSpan delay)
    {
        ManualTimer[] dueTimers;
        lock (_lock)
        {
            _elapsed += delay;
            dueTimers = _timers.Where(timer => timer.DueAt <= _elapsed).ToArray();
            _timers.RemoveAll(timer => timer.DueAt <= _elapsed);
        }

        foreach (var timer in dueTimers)
        {
            ThreadPool.UnsafeQueueUserWorkItem(static timer => timer.Fire(), timer, preferLocal: false);
        }
    }

    /// <summary>
    /// Waits until a timer is armed to fire within <paramref name="delay"/>, then advances the clock by it.
    /// </summary>
    /// <exception cref="TimeoutException">No such timer was armed within 30 seconds.</exception>
    public async Task AdvanceWhenArmedAsync(TimeSpan delay)
    {
        await WaitForTimerAsync(delay);
        Advance(delay);
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (period != Timeout.InfiniteTimeSpan)
        {
            throw new NotSupportedException("Only one-shot timers are supported.");
        }

        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    private void Arm(ManualTimer timer, TimeSpan dueTime)
    {
        TaskCompletionSource armed;
        lock (_lock)
        {
            _timers.Remove(timer);
            if (dueTime == Timeout.InfiniteTimeSpan)
            {
                return;
            }

            timer.DueAt = _elapsed + dueTime;
            _timers.Add(timer);
            armed = _timerArmed;
            _timerArmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        armed.TrySetResult();
    }

    private void Disarm(ManualTimer timer)
    {
        lock (_lock)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class ManualTimer(ManualTeardownClock clock, TimerCallback callback, object? state) : ITimer
    {
        public TimeSpan DueAt { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            clock.Arm(this, dueTime);
            return true;
        }

        public void Fire() => callback(state);

        public void Dispose() => clock.Disarm(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
