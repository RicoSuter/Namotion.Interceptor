namespace Namotion.Proxy.Handlers;

public static class DerivedPropertyChangeDetectionHandlerExtensions
{
    private const string UsedByPropertiesKey = "Namotion.Proxy.UsedByProperties.";
    private const string RequiredPropertiesKey = "Namotion.Proxy.RequiredProperties.";

    public static HashSet<TrackedProperty> GetUsedByProperties(this IProxy proxy, string propertyName)
    {
        return (HashSet<TrackedProperty>)proxy.Data.GetOrAdd(UsedByPropertiesKey + propertyName, (_) => new HashSet<TrackedProperty>())!;
    }

    public static HashSet<TrackedProperty> GetRequiredProperties(this IProxy proxy, string propertyName)
    {
        return (HashSet<TrackedProperty>)proxy.Data.GetOrAdd(RequiredPropertiesKey + propertyName, (_) => new HashSet<TrackedProperty>())!;
    }

    public static void SetRequiredProperties(this IProxy proxy, string propertyName, HashSet<TrackedProperty> requiredProperties)
    {
        proxy.Data[RequiredPropertiesKey + propertyName] = requiredProperties;
    }
}
