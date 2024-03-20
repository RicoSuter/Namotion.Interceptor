namespace Namotion.Proxy.Handlers;

public static class DerivedPropertyChangeDetectionHandlerExtensions
{
    private const string UsedByPropertiesKey = "Namotion.Proxy.UsedByProperties.";
    private const string RequiredPropertiesKey = "Namotion.Proxy.RequiredProperties.";
    private const string LastKnownValueKey = "Namotion.Proxy.LastKnownValue.";

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

    internal static object? GetLastKnownValue(this IProxy proxy, string propertyName)
    {
        return proxy.Data.GetOrAdd(LastKnownValueKey + propertyName, (_) => null)!;
    }

    internal static void SetLastKnownValue(this IProxy proxy, string propertyName, object? value)
    {
        proxy.Data[LastKnownValueKey + propertyName] = value;
    }
}
