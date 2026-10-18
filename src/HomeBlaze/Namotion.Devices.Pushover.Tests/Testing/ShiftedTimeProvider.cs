namespace Namotion.Devices.Pushover.Tests.Testing;

/// <summary>
/// The system time with offsets a test can change: <see cref="Advance"/> moves both clocks forward, as if that time
/// had passed, and <see cref="ShiftWallClock"/> moves only the wall clock, as a clock adjustment does.
/// </summary>
internal sealed class ShiftedTimeProvider : TimeProvider
{
    private long _wallClockOffsetTicks;
    private long _timestampOffset;

    public override long TimestampFrequency => System.TimestampFrequency;

    public override DateTimeOffset GetUtcNow() => System.GetUtcNow().AddTicks(Interlocked.Read(ref _wallClockOffsetTicks));

    public override long GetTimestamp() => System.GetTimestamp() + Interlocked.Read(ref _timestampOffset);

    public void Advance(TimeSpan duration)
    {
        ShiftWallClock(duration);
        Interlocked.Add(ref _timestampOffset, (long)(duration.TotalSeconds * TimestampFrequency));
    }

    public void ShiftWallClock(TimeSpan offset) => Interlocked.Add(ref _wallClockOffsetTicks, offset.Ticks);
}
