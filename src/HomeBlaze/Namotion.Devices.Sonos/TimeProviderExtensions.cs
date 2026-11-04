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
    internal static long GetTimestampAfter(this TimeProvider clock, TimeSpan delay) =>
        clock.AddToTimestamp(clock.GetTimestamp(), delay);

    /// <summary>
    /// Returns the timestamp the delay after the given one, saturating at <see cref="Never"/>.
    /// </summary>
    internal static long AddToTimestamp(this TimeProvider clock, long timestamp, TimeSpan delay)
    {
        var ticks = delay.TotalSeconds * clock.TimestampFrequency;
        return ticks >= Never - timestamp ? Never : timestamp + (long)ticks;
    }

    /// <summary>
    /// Returns the time from now until the timestamp, negative once it has passed.
    /// </summary>
    internal static TimeSpan GetTimeUntil(this TimeProvider clock, long timestamp) =>
        clock.GetElapsedTime(clock.GetTimestamp(), timestamp);
}
