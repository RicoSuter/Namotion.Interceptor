namespace Namotion.Interceptor.Tracking.Change;

public static class DerivedPropertyChangeHandlerExtensions
{
    private const string UsedByPropertiesKey = "Namotion.Interceptor.UsedByProperties";
    private const string RequiredPropertiesKey = "Namotion.Interceptor.RequiredProperties";
    private const string LastKnownValueKey = "Namotion.Interceptor.LastKnownValue";

    /// <summary>
    /// Gets a list of properties that use this property.
    /// </summary>
    /// <param name="property">The property.</param>
    /// <returns>The property list.</returns>
    public static HashSet<PropertyReference> GetUsedByProperties(this PropertyReference property)
    {
        return property.GetOrAddPropertyData(UsedByPropertiesKey, static () => new HashSet<PropertyReference>());
    }

    /// <summary>
    /// Sets the list of properties that are used by in this property's implementation (dependencies).
    /// </summary>
    /// <param name="property">The property.</param>
    /// <returns>The property list.</returns>
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
        return property.GetOrAddPropertyData(LastKnownValueKey, static () => new LastKnownValueWrapper()).Value;
    }

    // TODO(perf): Use a struct-based/ref approach somehow to avoid allocations here in SetLastKnownValue
    internal static void SetLastKnownValue(this PropertyReference property, object? value)
    {
        property.AddOrUpdatePropertyData<LastKnownValueWrapper, object?>(
            LastKnownValueKey, static (wrapper, val) => wrapper.Value = val, value);
    }
    
    // Wrapper used to avoid boxing when the stored value happens to be a value type
    // The wrapper itself is a reference type, so only one allocation per property is needed
    private class LastKnownValueWrapper
    {
        public object? Value { get; set; }
    }
}
