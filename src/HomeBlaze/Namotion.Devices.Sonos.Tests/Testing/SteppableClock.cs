namespace Namotion.Devices.Sonos.Tests.Testing;

/// <summary>
/// The system clock with a wall clock that a test can step, as an NTP correction would, while monotonic timestamps
/// keep running.
/// </summary>
internal sealed class SteppableClock : TimeProvider
{
    private long _offsetTicks;

    internal TimeSpan WallClockOffset
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));
        set => Interlocked.Exchange(ref _offsetTicks, value.Ticks);
    }

    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + WallClockOffset;
}
