namespace Namotion.Interceptor.Tracking;

public static class PropertyReferenceTimestampExtensions
{
    /// <summary>
    /// Gets the timestamp of the last write operation (requires <see cref="InterceptorSubjectContextExtensions.WithLifecycle"/> or <see cref="InterceptorSubjectContextExtensions.WithDerivedPropertyChangeDetection"/>).
    /// </summary>
    /// <param name="property">The property reference.</param>
    /// <returns>The timestamp.</returns>
    public static DateTimeOffset? TryGetWriteTimestamp(this PropertyReference property)
    {
        var ticks = property.GetWriteTimestampUtcTicks();
        return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    internal static void SetWriteTimestamp(this PropertyReference property, DateTimeOffset timestamp)
    {
        property.SetWriteTimestampUtcTicks(timestamp.UtcTicks);
    }
}