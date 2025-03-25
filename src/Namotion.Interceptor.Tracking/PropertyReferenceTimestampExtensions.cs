namespace Namotion.Interceptor.Tracking;

public static class PropertyReferenceTimestampExtensions
{
    private const string ChangedTimestampKey = "Namotion.Interceptor.ChangedTimestamp";

    public static DateTimeOffset? TryGetWriteTimestamp(this PropertyReference property)
    {
        return property.GetOrAddPropertyData(ChangedTimestampKey, () => (DateTimeOffset?)null);
    }

    internal static void SetWriteTimestamp(this PropertyReference property, DateTimeOffset timestamp)
    {
        property.SetPropertyData(ChangedTimestampKey, timestamp);
    }
}