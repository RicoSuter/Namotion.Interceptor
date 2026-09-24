namespace Namotion.Interceptor.Tracking;

/// <summary>
/// Describes the write that produced a value returned next to it. Fields may be added later.
/// </summary>
public readonly record struct PropertyValueMetadata
{
    internal PropertyValueMetadata(DateTimeOffset? writeTimestamp)
    {
        WriteTimestamp = writeTimestamp;
    }

    /// <summary>
    /// Creates the metadata from raw UTC ticks, where 0 means the property has never been written.
    /// </summary>
    internal PropertyValueMetadata(long writeTimestampTicks)
        : this(writeTimestampTicks == 0 ? null : new DateTimeOffset(writeTimestampTicks, TimeSpan.Zero))
    {
    }

    /// <summary>
    /// Gets the timestamp of the write that produced the value, or null if the property has never been written.
    /// </summary>
    public DateTimeOffset? WriteTimestamp { get; }
}
