namespace Namotion.Interceptor.Tracking;

public static class PropertyReferenceTimestampExtensions
{
    private const string WriteTimestampKey = "Namotion.Interceptor.WriteTimestamp";

    /// <summary>
    /// Gets the timestamp of the last write operation (requires <see cref="InterceptorSubjectContextExtensions.WithLifecycle"/> or <see cref="InterceptorSubjectContextExtensions.WithDerivedPropertyChangeDetection"/>).
    /// </summary>
    /// <param name="property">The property reference.</param>
    /// <returns>The timestamp.</returns>
    public static DateTimeOffset? TryGetWriteTimestamp(this PropertyReference property)
    {
        var holder = property.GetOrAddPropertyData(WriteTimestampKey, static () => new TimestampHolder());
        var ticks = Interlocked.Read(ref holder.Ticks);
        return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    internal static void SetWriteTimestamp(this PropertyReference property, DateTimeOffset timestamp)
    {
        var holder = property.GetOrAddPropertyData(WriteTimestampKey, static () => new TimestampHolder());
        Interlocked.Exchange(ref holder.Ticks, timestamp.UtcTicks);
    }

    // Holder for UTC ticks to avoid boxing and ensure thread-safe, allocation-free timestamp tracking.
    // Uses long (8 bytes) which is atomic on 64-bit platforms when accessed via Interlocked.
    // Value of 0 represents null (ticks=0 is year 0001, never a valid timestamp).
    private class TimestampHolder
    {
        public long Ticks;
    }
}