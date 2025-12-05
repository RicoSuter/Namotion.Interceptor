namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Extension methods for property timestamp tracking.
/// </summary>
public static class PropertyTimestampExtensions
{
    /// <summary>
    /// Gets the last changed timestamp for a property.
    /// </summary>
    /// <param name="property">The property reference.</param>
    /// <returns>The last changed timestamp, or null if never changed.</returns>
    public static DateTimeOffset? GetLastChangedTimestamp(this PropertyReference property)
    {
        if (property.TryGetPropertyData(PropertyTimestampInterceptor.LastChangedTimestampKey, out var value) &&
            value is DateTimeOffset timestamp)
        {
            return timestamp;
        }
        return null;
    }
}
