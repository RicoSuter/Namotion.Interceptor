namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Extension methods for derived property dependency tracking.
/// All data is consolidated into a single <see cref="DerivedPropertyData"/> object per property
/// stored with a short key to minimize dictionary lookup overhead.
/// </summary>
public static class DerivedPropertyChangeHandlerExtensions
{
    // Short key to minimize hash computation cost (6 chars vs 41+ chars for individual keys).
    // ConcurrentDictionary hash of (propertyName, key) processes every char of both strings.
    private const string DataKey = "ni.dpd";

    /// <summary>
    /// Gets the consolidated tracking data for a property, creating it if needed.
    /// Single dictionary lookup provides access to UsedByProperties, RequiredProperties, and LastKnownValue.
    /// </summary>
    internal static DerivedPropertyData GetDerivedPropertyData(this PropertyReference property) =>
        property.GetOrAddPropertyData(DataKey, static () => new DerivedPropertyData());

    /// <summary>
    /// Tries to get the consolidated tracking data without allocating if not present.
    /// Returns null if no tracking data has been registered for this property.
    /// </summary>
    internal static DerivedPropertyData? TryGetDerivedPropertyData(this PropertyReference property) =>
        property.TryGetPropertyData(DataKey, out var value) ? value as DerivedPropertyData : null;

    /// <summary>
    /// Gets backward dependencies: Which derived properties depend on this property.
    /// Example: If FullName depends on FirstName, then FirstName.GetUsedByProperties() includes FullName.
    /// </summary>
    public static DerivedPropertyDependencies GetUsedByProperties(this PropertyReference property) =>
        property.GetDerivedPropertyData().GetOrCreateUsedByProperties();

    /// <summary>
    /// Tries to get backward dependencies without allocating if not present.
    /// Returns null if no dependencies have been registered.
    /// </summary>
    internal static DerivedPropertyDependencies? TryGetUsedByProperties(this PropertyReference property) =>
        property.TryGetDerivedPropertyData()?.UsedByProperties;

    /// <summary>
    /// Gets forward dependencies: Which properties this derived property depends on.
    /// Example: FullName.GetRequiredProperties() includes FirstName and LastName.
    /// </summary>
    public static DerivedPropertyDependencies GetRequiredProperties(this PropertyReference property) =>
        property.GetDerivedPropertyData().GetOrCreateRequiredProperties();

    /// <summary>
    /// Tries to get forward dependencies without allocating if not present.
    /// Returns null if no dependencies have been registered.
    /// </summary>
    internal static DerivedPropertyDependencies? TryGetRequiredProperties(this PropertyReference property) =>
        property.TryGetDerivedPropertyData()?.RequiredProperties;

    /// <summary>
    /// Gets the cached last known value of a derived property.
    /// Used for change detection (compare old vs new value).
    /// </summary>
    internal static object? GetLastKnownValue(this PropertyReference property) =>
        property.TryGetDerivedPropertyData()?.LastKnownValue;

    /// <summary>
    /// Sets the cached last known value of a derived property.
    /// </summary>
    internal static void SetLastKnownValue(this PropertyReference property, object? value) =>
        property.GetDerivedPropertyData().LastKnownValue = value;
}
