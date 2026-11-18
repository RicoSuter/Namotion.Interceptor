namespace Namotion.Devices.Sonos.Tests.Testing;

/// <summary>
/// The system clock that a test can step: its wall clock alone, as an NTP correction would, while monotonic timestamps
/// keep running, or the whole clock forward, monotonic timestamps included. Timers still run in real time.
/// </summary>
internal sealed class SteppableClock : TimeProvider
{
    private long _offsetTicks;
    private long _monotonicOffsetTicks;

    internal TimeSpan WallClockOffset
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));
        set => Interlocked.Exchange(ref _offsetTicks, value.Ticks);
    }

    /// <summary>
    /// Moves the wall clock and the monotonic timestamps forward, as if the time had passed.
    /// </summary>
    internal void Advance(TimeSpan delay)
    {
        Interlocked.Add(ref _offsetTicks, delay.Ticks);
        Interlocked.Add(ref _monotonicOffsetTicks, delay.Ticks);
    }

    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + WallClockOffset;

    public override long GetTimestamp() =>
        base.GetTimestamp() + (long)(TimeSpan.FromTicks(Interlocked.Read(ref _monotonicOffsetTicks)).TotalSeconds * TimestampFrequency);
}
