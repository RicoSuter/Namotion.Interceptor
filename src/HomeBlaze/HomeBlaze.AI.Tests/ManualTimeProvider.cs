namespace HomeBlaze.AI.Tests;

/// <summary>A clock that only moves when a test advances it; its timers fire when an advance reaches their due time.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    private readonly Lock _lock = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = now;
    private int _createdTimerCount;

    /// <summary>The number of timers created so far, also of those that fired or were disposed.</summary>
    public int CreatedTimerCount => Volatile.Read(ref _createdTimerCount);

    /// <summary>The number of timers that are due in the future.</summary>
    public int PendingTimerCount
    {
        get
        {
            lock (_lock)
            {
                return _timers.Count;
            }
        }
    }

    public void Advance(TimeSpan time)
    {
        List<ManualTimer> dueTimers;
        lock (_lock)
        {
            _now += time;
            dueTimers = _timers.Where(timer => timer.DueTime <= _now).ToList();
            _timers.RemoveAll(dueTimers.Contains);
        }

        foreach (var timer in dueTimers)
        {
            timer.Fire();
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
        {
            return _now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        Interlocked.Increment(ref _createdTimerCount);
        return timer;
    }

    private void Schedule(ManualTimer timer, TimeSpan dueTime)
    {
        lock (_lock)
        {
            _timers.Remove(timer);
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                timer.DueTime = _now + dueTime;
                _timers.Add(timer);
            }
        }
    }

    // Periodic timers are not needed by the tests and fire once.
    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset DueTime { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            owner.Schedule(this, dueTime);
            return true;
        }

        public void Fire() => callback(state);

        public void Dispose() => owner.Schedule(this, Timeout.InfiniteTimeSpan);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
