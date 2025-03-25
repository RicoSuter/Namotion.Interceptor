namespace Namotion.Interceptor.Tracking;

public static class PropertyReferenceTimestampExtensions
{
    private const string ChangedTimestampKey = "Namotion.Interceptor.ChangedTimestamp";

    /// <summary>
    /// Gets the timestamp of the last write operation (requires <see cref="InterceptorSubjectContextExtensions.WithLifecycle"/> or <see cref="InterceptorSubjectContextExtensions.WithDerivedPropertyChangeDetection"/>).
    /// </summary>
    /// <param name="property">The property reference.</param>
    /// <returns>The timestamp.</returns>
    public static DateTimeOffset? TryGetWriteTimestamp(this PropertyReference property)
    {
        return property.GetOrAddPropertyData(ChangedTimestampKey, () => (DateTimeOffset?)null);
    }

    internal static void SetWriteTimestamp(this PropertyReference property, DateTimeOffset timestamp)
    {
        property.SetPropertyData(ChangedTimestampKey, timestamp);
    }
}