namespace Namotion.Proxy.ChangeTracking;

public static class DerivedPropertyChangeDetectionHandlerExtensions
{
    private const string UsedByPropertiesKey = "Namotion.Proxy.UsedByProperties.";
    private const string RequiredPropertiesKey = "Namotion.Proxy.RequiredProperties.";
    private const string LastKnownValueKey = "Namotion.Proxy.LastKnownValue.";

    public static HashSet<ProxyPropertyReference> GetUsedByProperties(this IProxy proxy, string propertyName)
    {
        return (HashSet<ProxyPropertyReference>)proxy.Data.GetOrAdd(UsedByPropertiesKey + propertyName, (_) => new HashSet<ProxyPropertyReference>())!;
    }

    public static HashSet<ProxyPropertyReference> GetRequiredProperties(this IProxy proxy, string propertyName)
    {
        return (HashSet<ProxyPropertyReference>)proxy.Data.GetOrAdd(RequiredPropertiesKey + propertyName, (_) => new HashSet<ProxyPropertyReference>())!;
    }

    public static void SetRequiredProperties(this IProxy proxy, string propertyName, HashSet<ProxyPropertyReference> requiredProperties)
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
