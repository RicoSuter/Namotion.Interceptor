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
        return property.GetOrAddPropertyData(WriteTimestampKey, () => new DateTimeOffsetWrapper()).Value;
    }

    internal static void SetWriteTimestamp(this PropertyReference property, DateTimeOffset timestamp)
    {
        property.AddOrUpdatePropertyData<DateTimeOffsetWrapper, DateTimeOffset>(
            WriteTimestampKey, static (wrapper, ts) => wrapper.Value = ts, timestamp);
    }
    
    // Wrapper used to avoid boxing of DateTimeOffset and ensure allocation free timestamp tracking
    private class DateTimeOffsetWrapper
    {
        public DateTimeOffset? Value { get; set; }
    }
}