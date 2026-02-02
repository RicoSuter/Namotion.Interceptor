namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Extension methods for derived property dependency tracking.
/// Stores dependency graph and cached values in PropertyReference metadata dictionary.
/// </summary>
public static class DerivedPropertyChangeHandlerExtensions
{
    private const string UsedByPropertiesKey = "Namotion.Interceptor.UsedByProperties";
    private const string RequiredPropertiesKey = "Namotion.Interceptor.RequiredProperties";
    private const string LastKnownValueKey = "Namotion.Interceptor.LastKnownValue";

    /// <summary>
    /// Gets backward dependencies: Which derived properties depend on this property.
    /// Example: If FullName depends on FirstName, then FirstName.GetUsedByProperties() includes FullName.
    /// </summary>
    public static DerivedPropertyDependencies GetUsedByProperties(this PropertyReference property) =>
        property.GetOrAddPropertyData(UsedByPropertiesKey, static () => new DerivedPropertyDependencies());

    /// <summary>
    /// Tries to get backward dependencies without allocating if not present.
    /// Returns null if no dependencies have been registered.
    /// </summary>
    internal static DerivedPropertyDependencies? TryGetUsedByProperties(this PropertyReference property) =>
        property.TryGetPropertyData(UsedByPropertiesKey, out var value) ? value as DerivedPropertyDependencies : null;

    /// <summary>
    /// Gets forward dependencies: Which properties this derived property depends on.
    /// Example: FullName.GetRequiredProperties() includes FirstName and LastName.
    /// </summary>
    public static DerivedPropertyDependencies GetRequiredProperties(this PropertyReference property) =>
        property.GetOrAddPropertyData(RequiredPropertiesKey, static () => new DerivedPropertyDependencies());

    /// <summary>
    /// Tries to get forward dependencies without allocating if not present.
    /// Returns null if no dependencies have been registered.
    /// </summary>
    internal static DerivedPropertyDependencies? TryGetRequiredProperties(this PropertyReference property) =>
        property.TryGetPropertyData(RequiredPropertiesKey, out var value) ? value as DerivedPropertyDependencies : null;

    /// <summary>
    /// Gets the cached last known value of a derived property.
    /// Used for change detection (compare old vs new value).
    /// </summary>
    internal static object? GetLastKnownValue(this PropertyReference property) =>
        property.GetOrAddPropertyData(LastKnownValueKey, static () => new LastKnownValueWrapper()).Value;

    /// <summary>
    /// Sets the cached last known value of a derived property.
    /// </summary>
    internal static void SetLastKnownValue(this PropertyReference property, object? value) =>
        property.AddOrUpdatePropertyData<LastKnownValueWrapper, object?>(
            LastKnownValueKey, static (wrapper, val) => wrapper.Value = val, value);

    /// <summary>
    /// Wrapper to avoid repeated boxing of value types.
    /// Allocated once per property (stored in metadata dict), then reused for all updates.
    /// </summary>
    private sealed class LastKnownValueWrapper
    {
        public object? Value { get; set; }
    }
}
