namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Consolidated per-property data for derived property tracking.
/// Stored once per (subject, property) in Subject.Data under a short key
/// to minimize dictionary lookups (one lookup instead of separate lookups
/// for UsedByProperties, RequiredProperties, and LastKnownValue).
/// </summary>
internal sealed class DerivedPropertyData
{
    /// <summary>
    /// Backward dependencies: Which derived properties depend on this property.
    /// Initialized lazily via Interlocked.CompareExchange for thread safety.
    /// </summary>
    public DerivedPropertyDependencies? UsedByProperties;

    /// <summary>
    /// Forward dependencies: Which properties this derived property depends on.
    /// Initialized lazily via Interlocked.CompareExchange for thread safety.
    /// </summary>
    public DerivedPropertyDependencies? RequiredProperties;

    /// <summary>
    /// Cached last known value for change detection.
    /// Only used for derived properties.
    /// </summary>
    public object? LastKnownValue;

    /// <summary>
    /// Gets or creates the UsedByProperties collection (thread-safe).
    /// </summary>
    public DerivedPropertyDependencies GetOrCreateUsedByProperties()
    {
        var usedBy = Volatile.Read(ref UsedByProperties);
        if (usedBy is not null)
        {
            return usedBy;
        }

        var created = new DerivedPropertyDependencies();
        return Interlocked.CompareExchange(ref UsedByProperties, created, null) ?? created;
    }

    /// <summary>
    /// Gets or creates the RequiredProperties collection (thread-safe).
    /// </summary>
    public DerivedPropertyDependencies GetOrCreateRequiredProperties()
    {
        var required = Volatile.Read(ref RequiredProperties);
        if (required is not null)
        {
            return required;
        }

        var created = new DerivedPropertyDependencies();
        return Interlocked.CompareExchange(ref RequiredProperties, created, null) ?? created;
    }
}
