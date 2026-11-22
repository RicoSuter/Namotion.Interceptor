using Microsoft.Extensions.Time.Testing;

namespace Namotion.Devices.Hyundai.Tests.Testing;

/// <summary>
/// A fake clock that knows its pending timers, so that a test can advance it to the next delay of the code under test
/// without firing the timeout of a request in flight.
/// </summary>
internal sealed class TestTimeProvider(DateTimeOffset start) : FakeTimeProvider(start)
{
    private readonly Lock _lock = new();
    private readonly HashSet<PendingTimer> _timers = [];

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new PendingTimer(this);
        timer.Schedule(dueTime, period);
        timer.Inner = base.CreateTimer(_ =>
        {
            timer.OnFired();
            callback(state);
        }, null, dueTime, period);
        return timer;
    }

    /// <summary>
    /// Advances to the earliest pending timer that is shorter than <paramref name="maximumDuration"/>, unless a longer
    /// timer is due at or before it, so that no longer timer ever fires.
    /// </summary>
    /// <returns>Whether the clock advanced.</returns>
    public bool AdvanceToNextTimer(TimeSpan maximumDuration)
    {
        DateTimeOffset? next;
        DateTimeOffset? nextLong;
        lock (_lock)
        {
            next = _timers.Where(timer => timer.Duration < maximumDuration).Min(timer => timer.DueAt);
            nextLong = _timers.Where(timer => timer.Duration >= maximumDuration).Min(timer => timer.DueAt);
        }

        if (next is not { } target || target <= GetUtcNow() || nextLong <= target)
        {
            return false;
        }

        Advance(target - GetUtcNow());
        return true;
    }

    private sealed class PendingTimer(TestTimeProvider owner) : ITimer
    {
        private TimeSpan _period;
        private bool _isDisposed;

        public ITimer Inner { get; set; } = null!;

        /// <summary>The time the timer fires next, or null when it is stopped, fired once or disposed.</summary>
        public DateTimeOffset? DueAt { get; private set; }

        public TimeSpan Duration { get; private set; }

        public void Schedule(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._lock)
            {
                if (_isDisposed)
                {
                    return;
                }

                Duration = dueTime;
                _period = period;
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner.GetUtcNow() + dueTime;
                if (DueAt is null)
                {
                    owner._timers.Remove(this);
                }
                else
                {
                    owner._timers.Add(this);
                }
            }
        }

        public void OnFired()
        {
            lock (owner._lock)
            {
                if (_period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero)
                {
                    DueAt = null;
                    owner._timers.Remove(this);
                }
                else
                {
                    DueAt = (DueAt ?? owner.GetUtcNow()) + _period;
                }
            }
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Schedule(dueTime, period);
            return Inner.Change(dueTime, period);
        }

        public void Dispose()
        {
            Stop();
            Inner.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Stop();
            return Inner.DisposeAsync();
        }

        private void Stop()
        {
            lock (owner._lock)
            {
                _isDisposed = true;
                DueAt = null;
                owner._timers.Remove(this);
            }
        }
    }
}
