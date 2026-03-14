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
    internal DerivedPropertyDependencies? UsedByProperties;

    /// <summary>
    /// Forward dependencies: Which properties this derived property depends on.
    /// Always read/written under lock(this) — no volatile or CAS needed.
    /// Null until first recalculation; replaced atomically on each recalculation.
    /// </summary>
    internal PropertyReference[]? RequiredProperties;

    /// <summary>
    /// Cached last known value for change detection.
    /// Only used for derived properties.
    /// </summary>
    internal object? LastKnownValue;

    /// <summary>
    /// Re-entrancy guard for RecalculateDerivedProperty.
    /// Prevents infinite recursion when a derived-with-setter property's
    /// SetPropertyValueWithInterception re-enters WriteProperty.
    /// Only read/written inside lock(this), so no volatile needed.
    /// </summary>
    internal bool IsRecalculating;

    /// <summary>
    /// Lifecycle flag cleared during DetachProperty under lock(this).
    /// Checked by RecalculateDerivedProperty to prevent zombie backlink resurrection.
    /// Set by AttachProperty to support re-attachment.
    /// Defaults to true because properties are assumed live until explicitly detached.
    /// </summary>
    internal bool IsAttached = true;

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
}
