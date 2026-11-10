namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// The one timer of the worker of an automation: set to the instant the worker is due next, at most
/// <see cref="MaxWakeInterval"/> ahead, and stopped while nothing is due. Its callback only enqueues. Set by the worker
/// only; set again after every callback, also when the full queue dropped its tick.
/// </summary>
internal sealed class WakeTimer : IDisposable
{
    /// <summary>
    /// How long the worker sleeps at most while something is due, so that it notices within this time that the wall
    /// clock was set, which the timer does not follow.
    /// </summary>
    public static readonly TimeSpan MaxWakeInterval = TimeSpan.FromMinutes(1);

    private readonly ITimer _timer;
    private DateTimeOffset? _scheduledDueAt;

    // Cleared by every callback, also one whose tick the full queue drops, so that the worker sets the timer again.
    private volatile bool _isScheduled;

    public WakeTimer(TimeProvider timeProvider, Action wake) =>
        _timer = timeProvider.CreateTimer(
            _ =>
            {
                _isScheduled = false;
                wake();
            },
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);

    /// <summary>
    /// Sets the timer to <paramref name="dueAt"/>, or stops it for null; does nothing when it is set to that future
    /// instant already or is stopped and nothing is due, so that most inputs cost one comparison here.
    /// </summary>
    public void Schedule(DateTimeOffset? dueAt, DateTimeOffset now)
    {
        // While not scheduled, the one-shot timer is not set: only its callback and a stop clear the flag.
        if (dueAt is null ? !_isScheduled : _isScheduled && dueAt == _scheduledDueAt && dueAt > now)
        {
            return;
        }

        _scheduledDueAt = dueAt;
        if (dueAt is not { } due)
        {
            _isScheduled = false;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }

        // Set before the timer, so that a callback that comes at once clears it. The delay is rounded up to whole
        // milliseconds, which the timer counts, so that a wake-up that comes early sets a delay of at least one
        // millisecond again instead of zero until the instant passed.
        _isScheduled = true;
        var delay = due - now;
        var milliseconds = delay <= TimeSpan.Zero ? 0 : Math.Ceiling(delay.TotalMilliseconds);
        _timer.Change(TimeSpan.FromMilliseconds(Math.Min(milliseconds, MaxWakeInterval.TotalMilliseconds)), Timeout.InfiniteTimeSpan);
    }

    public void Dispose() => _timer.Dispose();
}
