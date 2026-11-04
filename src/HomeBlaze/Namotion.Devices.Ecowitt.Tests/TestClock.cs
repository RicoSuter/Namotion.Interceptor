namespace Namotion.Devices.Ecowitt.Tests;

internal sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; }

    public override DateTimeOffset GetUtcNow() => Now;
}
