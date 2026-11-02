namespace Namotion.Interceptor;

/// <summary>
/// Describes the write that produced a value returned next to it. Fields may be added later.
/// </summary>
public readonly struct PropertyValueMetadata
{
    private readonly long _writeTimestampTicks;

    /// <summary>
    /// Creates the metadata from raw UTC ticks, where 0 means no timestamp.
    /// </summary>
    internal PropertyValueMetadata(long writeTimestampTicks)
    {
        _writeTimestampTicks = writeTimestampTicks;
    }

    /// <summary>
    /// Gets the timestamp of the write that produced the value, or of a later write, or null if that write
    /// had no timestamp or the property has never been written;
    /// see <see cref="PropertyReference.GetValue(out PropertyValueMetadata)"/>.
    /// </summary>
    public DateTimeOffset? WriteTimestamp => PropertyWriteState.ToTimestamp(_writeTimestampTicks);
}
