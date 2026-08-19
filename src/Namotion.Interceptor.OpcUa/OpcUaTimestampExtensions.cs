namespace Namotion.Interceptor.OpcUa;

internal static class OpcUaTimestampExtensions
{
    /// <summary>
    /// Converts an inbound OPC UA timestamp, pinning its kind to UTC. OPC UA timestamps are UTC by
    /// specification, but the SDK decodes the two boundary wire values to <see cref="DateTime.MinValue"/>
    /// and <see cref="DateTime.MaxValue"/> with an unspecified kind, which the plain conversion reads as
    /// local time and then throws on: the first east of UTC, the second west of it.
    /// </summary>
    public static DateTimeOffset ToUtcDateTimeOffset(this DateTime timestamp)
    {
        return new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc));
    }
}
