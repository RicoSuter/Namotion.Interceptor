namespace Namotion.Proxy.Handlers;

public static class DetectDerivedPropertyChangesHandlerExtensions
{
    private const string UsedByPropertiesKey = "Namotion.Proxy.UsedByProperties";
    private const string RequiredPropertiesKey = "Namotion.Proxy.RequiredProperties";

    public static HashSet<TrackedProperty> GetUsedByProperties(this IProxy proxy)
    {
        return (HashSet<TrackedProperty>)proxy.Data.GetOrAdd(UsedByPropertiesKey, (_) => new HashSet<TrackedProperty>())!;
    }

    public static HashSet<TrackedProperty> GetRequiredProperties(this IProxy proxy)
    {
        return (HashSet<TrackedProperty>)proxy.Data.GetOrAdd(RequiredPropertiesKey, (_) => new HashSet<TrackedProperty>())!;
    }

    public static void SetRequiredProperties(this IProxy proxy, HashSet<TrackedProperty> requiredProperties)
    {
        proxy.Data[RequiredPropertiesKey] = requiredProperties;
    }
}
