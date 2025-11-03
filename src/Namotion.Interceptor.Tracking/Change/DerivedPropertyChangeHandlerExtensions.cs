namespace Namotion.Interceptor.Tracking.Change;

public static class DerivedPropertyChangeHandlerExtensions
{
    private const string UsedByPropertiesKey = "Namotion.Interceptor.UsedByProperties";
    private const string RequiredPropertiesKey = "Namotion.Interceptor.RequiredProperties";
    private const string LastKnownValueKey = "Namotion.Interceptor.LastKnownValue";

    /// <summary>
    /// Gets the lock-free collection of properties that depend on this property (used-by relationships).
    /// Reading is allocation-free; modifications use lock-free CAS.
    /// </summary>
    public static DerivedPropertyDependencies GetUsedByProperties(this PropertyReference property)
    {
        return property.GetOrAddPropertyData(UsedByPropertiesKey, static () => new DerivedPropertyDependencies());
    }

    /// <summary>
    /// Gets the lock-free collection of properties that this property depends on (required dependencies).
    /// Reading is allocation-free; modifications use lock-free CAS.
    /// </summary>
    public static DerivedPropertyDependencies GetRequiredProperties(this PropertyReference property)
    {
        return property.GetOrAddPropertyData(RequiredPropertiesKey, static () => new DerivedPropertyDependencies());
    }

    /// <summary>
    /// Updates the required dependencies for this property from recorded accesses.
    /// </summary>
    internal static void SetRequiredProperties(this PropertyReference property, ReadOnlySpan<PropertyReference> dependencies)
    {
        property.GetRequiredProperties().ReplaceWith(dependencies);
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
