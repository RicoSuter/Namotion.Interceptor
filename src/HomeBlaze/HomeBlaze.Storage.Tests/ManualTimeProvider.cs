namespace HomeBlaze.Storage.Tests;

/// <summary>
/// A time provider whose clock only moves through <see cref="Advance"/>, which runs due timers synchronously.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly Lock _lock = new();
    private readonly List<ManualTimer> _timers = [];
    private long _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>
    /// The number of timers that are waiting to fire.
    /// </summary>
    public int ArmedTimerCount
    {
        get
        {
            lock (_lock)
            {
                return _timers.Count(timer => timer.DueTimestamp is not null);
            }
        }
    }

    public override long GetTimestamp()
    {
        lock (_lock)
        {
            return _timestamp;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_lock)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan duration)
    {
        long target;
        lock (_lock)
        {
            target = _timestamp + duration.Ticks;
        }

        while (true)
        {
            ManualTimer? dueTimer;
            lock (_lock)
            {
                dueTimer = _timers
                    .Where(timer => timer.DueTimestamp <= target)
                    .MinBy(timer => timer.DueTimestamp);

                if (dueTimer is null)
                {
                    _timestamp = target;
                    return;
                }

                _timestamp = Math.Max(_timestamp, dueTimer.DueTimestamp!.Value);
                dueTimer.DueTimestamp = dueTimer.Period is { } period ? _timestamp + period.Ticks : null;
            }

            dueTimer.Invoke();
        }
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _timeProvider;
        private readonly TimerCallback _callback;
        private readonly object? _state;

        public ManualTimer(ManualTimeProvider timeProvider, TimerCallback callback, object? state)
        {
            _timeProvider = timeProvider;
            _callback = callback;
            _state = state;
        }

        public long? DueTimestamp { get; set; }

        public TimeSpan? Period { get; private set; }

        public void Invoke() => _callback(_state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_timeProvider._lock)
            {
                DueTimestamp = dueTime == Timeout.InfiniteTimeSpan ? null : _timeProvider._timestamp + dueTime.Ticks;
                Period = period == Timeout.InfiniteTimeSpan || period == TimeSpan.Zero ? null : period;
                return true;
            }
        }

        public void Dispose()
        {
            lock (_timeProvider._lock)
            {
                DueTimestamp = null;
                _timeProvider._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
