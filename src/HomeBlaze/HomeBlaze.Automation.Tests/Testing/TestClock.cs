using Microsoft.Extensions.Time.Testing;

namespace HomeBlaze.Automation.Tests.Testing;

/// <summary>
/// The clock of the automations of a test: the fake time drives the timers, the wall clock can be set apart from it as
/// a correction of the system clock does, and the callbacks of the timers are counted as wake-ups.
/// </summary>
internal sealed class TestClock(FakeTimeProvider time) : TimeProvider
{
    private int _wakeUps;
    private long _wallClockShiftTicks;

    /// <summary>How far the wall clock is ahead of the fake time, or behind it when negative.</summary>
    public TimeSpan WallClockShift
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _wallClockShiftTicks));
        set => Interlocked.Exchange(ref _wallClockShiftTicks, value.Ticks);
    }

    public int WakeUps => Volatile.Read(ref _wakeUps);

    public override TimeZoneInfo LocalTimeZone => time.LocalTimeZone;

    public override long TimestampFrequency => time.TimestampFrequency;

    public override DateTimeOffset GetUtcNow() => time.GetUtcNow() + WallClockShift;

    public override long GetTimestamp() => time.GetTimestamp();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        time.CreateTimer(
            callbackState =>
            {
                Interlocked.Increment(ref _wakeUps);
                callback(callbackState);
            },
            state,
            dueTime,
            period);
}
