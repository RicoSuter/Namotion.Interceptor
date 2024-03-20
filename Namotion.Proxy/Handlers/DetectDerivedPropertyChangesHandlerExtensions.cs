namespace Namotion.Proxy.Handlers;

public static class DetectDerivedPropertyChangesHandlerExtensions
{
    public static HashSet<TrackedProperty> GetUsedByProperties(this IProxy proxy)
    {
        return (HashSet<TrackedProperty>)proxy.Data.GetOrAdd("UsedByProperties", (_) => new HashSet<TrackedProperty>())!;
    }

    public static HashSet<TrackedProperty> GetRequiredProperties(this IProxy proxy)
    {
        return (HashSet<TrackedProperty>)proxy.Data.GetOrAdd("RequiredProperties", (_) => new HashSet<TrackedProperty>())!;
    }

    public static void SetRequiredProperties(this IProxy proxy, HashSet<TrackedProperty> requiredProperties)
    {
        proxy.Data["RequiredProperties"] = requiredProperties;
    }
}
