namespace Namotion.Interceptor.Tracking;

/// <summary>
/// Describes the write that produced a value returned next to it. Fields may be added later.
/// </summary>
public readonly record struct PropertyValueMetadata
{
    public PropertyValueMetadata(DateTimeOffset? writeTimestamp)
    {
        WriteTimestamp = writeTimestamp;
    }

    /// <summary>
    /// Gets the timestamp of the write that produced the value, or null if the property has never been written.
    /// </summary>
    public DateTimeOffset? WriteTimestamp { get; }
}
