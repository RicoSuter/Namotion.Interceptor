namespace Namotion.Devices.Pushover.Tests.Testing;

/// <summary>
/// The system time with a wall clock offset a test can change with <see cref="ShiftWallClock"/>, as a clock adjustment
/// does; the timestamps stay those of the system.
/// </summary>
internal sealed class ShiftedTimeProvider : TimeProvider
{
    private long _wallClockOffsetTicks;

    public override DateTimeOffset GetUtcNow() => System.GetUtcNow().AddTicks(Interlocked.Read(ref _wallClockOffsetTicks));

    public void ShiftWallClock(TimeSpan offset) => Interlocked.Add(ref _wallClockOffsetTicks, offset.Ticks);
}
