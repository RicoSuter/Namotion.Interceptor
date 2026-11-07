namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// The one timer of the worker of an automation: set to the instant the worker is due next, at most
/// <see cref="MaxWakeInterval"/> ahead, and stopped while nothing is due. Its callback only enqueues. Set by the worker
/// only.
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
    private bool _isScheduled;

    public WakeTimer(TimeProvider timeProvider, Action wake) =>
        _timer = timeProvider.CreateTimer(_ => wake(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    /// <summary>
    /// Sets the timer to <paramref name="dueAt"/>, or stops it for null; does nothing when it is set to that instant
    /// already, so that most inputs cost one comparison here.
    /// </summary>
    public void Schedule(DateTimeOffset? dueAt, DateTimeOffset now)
    {
        if (_isScheduled && dueAt == _scheduledDueAt)
        {
            return;
        }

        _scheduledDueAt = dueAt;
        _isScheduled = dueAt is not null;
        if (dueAt is not { } due)
        {
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }

        var delay = due - now;
        if (delay < TimeSpan.Zero)
        {
            delay = TimeSpan.Zero;
        }

        _timer.Change(delay > MaxWakeInterval ? MaxWakeInterval : delay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Called when the worker takes the tick of a wake-up, after which the timer is set again.</summary>
    public void OnWokenUp() => _isScheduled = false;

    public void Dispose() => _timer.Dispose();
}
