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
    public static PropertyReferenceCollection GetUsedByProperties(this PropertyReference property) =>
        property.TryGetDerivedPropertyData()?.UsedByDependencies ?? PropertyReferenceCollection.Empty;

    /// <summary>
    /// Gets forward dependencies: Which properties this derived property depends on.
    /// Example: FullName.GetRequiredProperties() includes FirstName and LastName.
    /// Returns empty span if no tracking data exists (allocation-free).
    /// </summary>
    public static ReadOnlySpan<PropertyReference> GetRequiredProperties(this PropertyReference property)
    {
        var data = property.TryGetDerivedPropertyData();
        return data is not null ? data.RequiredPropertiesSpan : ReadOnlySpan<PropertyReference>.Empty;
    }

    /// <summary>
    /// Recalculates a derived property by re-evaluating its getter and firing change
    /// notifications if the value changed. Use this when the getter depends on external
    /// (non-intercepted) data and that data has changed.
    /// No-op if the property is not a derived property or is not attached to a context.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RecalculateDerivedProperty(this PropertyReference property)
    {
        var data = property.TryGetDerivedPropertyData();
        if (data is null || !Volatile.Read(ref data.IsDerived))
        {
            return;
        }

        // Pass storage timestamp as both storage and raw. For positive and no scope this is a
        // positive value that seeds the dependent context's cache directly. For null scope it
        // is 0 (the cache's uninitialized sentinel), which makes the dependent's terminal
        // write lazy-resolve once; the resolved value then threads through any further
        // cascade dependents via WriteTimestampRaw, so every change event from this recalc
        // still shares a single publishing time (verified by mock-now tests in this file).
        var storageTimestamp = SubjectChangeContext.Current.ResolveChangedTimestamp();
        DerivedPropertyChangeHandler.RecalculateDerivedProperty(ref property, storageTimestamp, storageTimestamp);
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
