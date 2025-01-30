namespace Namotion.Interceptor.Tracking.Change;

public static class DerivedPropertyChangeHandlerExtensions
{
    private const string UsedByPropertiesKey = "Namotion.Interceptor.UsedByProperties";
    private const string RequiredPropertiesKey = "Namotion.Interceptor.RequiredProperties";
    private const string LastKnownValueKey = "Namotion.Interceptor.LastKnownValue";

    public static HashSet<PropertyReference> GetUsedByProperties(this PropertyReference property)
    {
        return property.GetOrAddPropertyData(UsedByPropertiesKey, () => new HashSet<PropertyReference>());
    }

    public static HashSet<PropertyReference> GetRequiredProperties(this PropertyReference property)
    {
        return property.GetOrAddPropertyData(RequiredPropertiesKey, () => new HashSet<PropertyReference>());
    }

    internal static void SetRequiredProperties(this PropertyReference property, HashSet<PropertyReference> requiredProperties)
    {
        property.SetPropertyData(RequiredPropertiesKey, requiredProperties);
    }

    internal static object? GetLastKnownValue(this PropertyReference property)
    {
        return property.GetOrAddPropertyData(LastKnownValueKey, () => (object?)null);
    }

    internal static void SetLastKnownValue(this PropertyReference property, object? value)
    {
        property.SetPropertyData(LastKnownValueKey, value);
    }
}
