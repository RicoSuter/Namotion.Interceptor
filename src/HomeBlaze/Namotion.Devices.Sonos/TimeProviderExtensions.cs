namespace Namotion.Devices.Sonos;

/// <summary>
/// Monotonic scheduling on <see cref="TimeProvider.GetTimestamp"/>, which a wall-clock step such as an NTP correction
/// does not move.
/// </summary>
internal static class TimeProviderExtensions
{
    /// <summary>
    /// The timestamp for something that is not scheduled.
    /// </summary>
    internal const long Never = long.MaxValue;

    /// <summary>
    /// Returns the timestamp the delay from now, saturating at <see cref="Never"/>.
    /// </summary>
    internal static long GetTimestampAfter(this TimeProvider clock, TimeSpan delay)
    {
        var now = clock.GetTimestamp();
        var ticks = delay.TotalSeconds * clock.TimestampFrequency;
        return ticks >= Never - now ? Never : now + (long)ticks;
    }

    /// <summary>
    /// Returns the time from now until the timestamp, negative once it has passed.
    /// </summary>
    internal static TimeSpan GetTimeUntil(this TimeProvider clock, long timestamp) =>
        clock.GetElapsedTime(clock.GetTimestamp(), timestamp);
}
