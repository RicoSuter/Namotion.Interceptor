using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Extension methods for derived property dependency tracking.
/// All data is consolidated into a single <see cref="DerivedPropertyData"/> object per property
/// stored with a short key to minimize dictionary lookup overhead.
/// </summary>
public static class DerivedPropertyChangeHandlerExtensions
{
    // Short key to reduce dictionary hash cost on this hot path (verified).
    private const string DataKey = "ni.dpd";

    /// <summary>
    /// Gets backward dependencies: Which derived properties depend on this property.
    /// Example: If FullName depends on FirstName, then FirstName.GetUsedByProperties() includes FullName.
    /// Returns a shared empty instance if no tracking data exists (allocation-free).
    /// </summary>
    public static DerivedPropertyDependencies GetUsedByProperties(this PropertyReference property) =>
        property.TryGetDerivedPropertyData()?.UsedByProperties ?? DerivedPropertyDependencies.Empty;

    /// <summary>
    /// Gets forward dependencies: Which properties this derived property depends on.
    /// Example: FullName.GetRequiredProperties() includes FirstName and LastName.
    /// Returns empty span if no tracking data exists (allocation-free).
    /// </summary>
    public static ReadOnlySpan<PropertyReference> GetRequiredProperties(this PropertyReference property)
    {
        var items = property.TryGetDerivedPropertyData()?.RequiredProperties;
        return items is not null ? items.AsSpan() : ReadOnlySpan<PropertyReference>.Empty;
    }

    /// <summary>
    /// Gets the consolidated tracking data for a property, creating it if needed.
    /// A single dictionary lookup provides access to UsedByProperties, RequiredProperties, and LastKnownValue.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static DerivedPropertyData GetDerivedPropertyData(this PropertyReference property) =>
        property.GetOrAddPropertyData(DataKey, static () => new DerivedPropertyData());

    /// <summary>
    /// Tries to get the consolidated tracking data without allocating if not present.
    /// Returns null if no tracking data has been registered for this property.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static DerivedPropertyData? TryGetDerivedPropertyData(this PropertyReference property) =>
        property.TryGetPropertyData(DataKey, out var value) ? value as DerivedPropertyData : null;

}
