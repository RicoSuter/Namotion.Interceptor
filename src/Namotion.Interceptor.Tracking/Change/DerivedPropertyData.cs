using System.Runtime.CompilerServices;

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
    /// Dependencies: Which properties this derived property depends on.
    /// Always read/written under lock(this) — no volatile or CAS needed.
    /// Null until first recalculation; buffer may be larger than count for reuse.
    /// </summary>
    private PropertyReference[]? _requiredProperties;

    /// <summary>
    /// Number of valid entries in _requiredProperties (may be less than array length).
    /// Always read/written under lock(this).
    /// </summary>
    private int _requiredPropertyCount;

    /// <summary>
    /// Used-by properties: Which derived properties depend on this property.
    /// Initialized lazily via Interlocked.CompareExchange for thread safety.
    /// </summary>
    private PropertyReferenceCollection? _usedByProperties;

    /// <summary>
    /// Cached last known value for change detection.
    /// Only used for derived properties.
    /// </summary>
    internal object? LastKnownValue;

    /// <summary>
    /// Incremented by every landed write of this property (source role), after the value landed and a full fence;
    /// read by evaluations to detect a write between a read and its link.
    /// </summary>
    internal long Version;

    /// <summary>
    /// Reentrancy guard for RecalculateDerivedProperty.
    /// Prevents infinite recursion when a derived-with-setter property's
    /// PublishDerivedPropertyValue re-enters WriteProperty.
    /// Only read/written inside lock(this), so no volatile needed.
    /// </summary>
    internal bool IsRecalculating;

    /// <summary>
    /// Sequence counter incremented under lock(this) each time RecalculateDerivedProperty
    /// computes a new value. Read via Volatile.Read outside the lock to detect stale
    /// notifications — if the sequence has advanced, a newer recalculation is already completed
    /// and the current notification should be skipped.
    /// </summary>
    internal long RecalculationSequence;

    /// <summary>
    /// Set under lock(this) when a state change occurs while IsRecalculating is true:
    /// concurrent RecalculateDerivedProperty (bails), DetachProperty, or AttachProperty.
    /// Checked by RecalculateDerivedProperty before committing — if set, the evaluation
    /// result is discarded and the getter is re-evaluated with a fresh state.
    /// </summary>
    internal bool RecalculationNeeded;

    /// <summary>
    /// Lifecycle flag cleared during DetachProperty under lock(this).
    /// Checked by RecalculateDerivedProperty to prevent zombie used-by property resurrection.
    /// Set by AttachProperty on every attach of a derived property. Not read for source properties,
    /// whose data a detach always removes.
    /// Defaults to true because properties are assumed to be attached until explicitly detached.
    /// </summary>
    internal bool IsAttached = true;

    /// <summary>
    /// True if this data belongs to a derived property (vs. a source property whose data exists only
    /// because a derived depends on it). Lets WriteProperty identify derived-with-setter writes
    /// without touching metadata on the hot path.
    /// </summary>
    /// <remarks>
    /// Written via <c>Volatile.Write</c> in AttachProperty, read via <c>Volatile.Read</c> in
    /// WriteProperty. Write-once; never reset (metadata.IsDerived is immutable).
    /// </remarks>
    internal bool IsDerived;

    /// <summary>
    /// Read-only access to used-by properties for public API.
    /// </summary>
    internal PropertyReferenceCollection? UsedByDependencies
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref _usedByProperties);
    }

    /// <summary>
    /// Returns the current dependencies as a read-only span.
    /// Returns an empty span if no dependencies exist (allocation-free).
    /// Safe for lock-free reads (clamps count to array length to handle torn reads).
    /// </summary>
    internal ReadOnlySpan<PropertyReference> RequiredPropertiesSpan
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var array = _requiredProperties;
            if (array is null)
            {
                return ReadOnlySpan<PropertyReference>.Empty;
            }

            return array.AsSpan(0, Math.Min(_requiredPropertyCount, array.Length));
        }
    }

    /// <summary>
    /// Returns used-by properties as a read-only span (allocation-free).
    /// Performs a single Volatile.Read of the collection and returns its Items span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ReadOnlySpan<PropertyReference> GetUsedByProperties()
    {
        var usedBy = Volatile.Read(ref _usedByProperties);
        if (usedBy is null)
        {
            return ReadOnlySpan<PropertyReference>.Empty;
        }

        return usedBy.Items;
    }

    /// <summary>
    /// Detaches this property: sets IsAttached=false, cleans dependencies (if derived),
    /// snapshots, and clears used-by properties. Called under lock(this).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal PropertyReference[] DetachAndSnapshotUsedBy(in PropertyReference property)
    {
        IsAttached = false;

        // Case 1 (derived only): remove this property from each dependency's UsedByProperties.
        if (property.Metadata.IsDerived)
        {
            var requiredProperties = RequiredPropertiesSpan;
            for (var i = 0; i < requiredProperties.Length; i++)
            {
                requiredProperties[i]
                    .TryGetDerivedPropertyData()?
                    .RemoveUsedByProperty(property);
            }

            ClearRequiredProperties();
            LastKnownValue = null;
        }

        // Case 2: snapshot used-by properties, then clear. The snapshot is stable because the collection
        // never changes an array it handed out.
        var snapshot = _usedByProperties?.ItemsArray ?? [];
        _usedByProperties = null;
        return snapshot;
    }

    /// <summary>
    /// Updates dependencies (RequiredProperties) and used-by properties (UsedByProperties) to the recorded set.
    /// Called under lock(this). Returns true if a dependency was added or removed.
    /// </summary>
    internal bool UpdateDependencies(in PropertyReference derivedProperty, ReadOnlySpan<RecordedDependency> recorded)
    {
        var previous = RequiredPropertiesSpan;
        if (previous.Length == recorded.Length && IsSameOrder(previous, recorded))
        {
            return false;
        }

        var isChanged = false;
        foreach (ref readonly var old in previous)
        {
            if (IndexOf(recorded, old) < 0)
            {
                old.TryGetDerivedPropertyData()?.RemoveUsedByProperty(derivedProperty);
                isChanged = true;
            }
        }

        foreach (ref readonly var dependency in recorded)
        {
            if (previous.IndexOf(dependency.Property) < 0)
            {
                AddUsedByPropertyToCurrentData(dependency.Property, derivedProperty);
                isChanged = true;
            }
        }

        SetRequiredProperties(recorded);
        return isChanged;
    }

    private static bool IsSameOrder(ReadOnlySpan<PropertyReference> previous, ReadOnlySpan<RecordedDependency> recorded)
    {
        for (var i = 0; i < previous.Length; i++)
        {
            if (previous[i] != recorded[i].Property)
            {
                return false;
            }
        }

        return true;
    }

    private static int IndexOf(ReadOnlySpan<RecordedDependency> recorded, in PropertyReference property)
    {
        for (var i = 0; i < recorded.Length; i++)
        {
            if (recorded[i].Property == property)
            {
                return i;
            }
        }

        return -1;
    }

    private static void AddUsedByPropertyToCurrentData(in PropertyReference dependency, in PropertyReference derivedProperty)
    {
        // A detach may remove the fetched data before it is locked. Registering on the removed instance would
        // lose this used-by property when the dependency is attached again, so register on the current data.
        while (true)
        {
            var dependencyData = dependency.GetDerivedPropertyData();
            lock (dependencyData)
            {
                if (ReferenceEquals(dependency.TryGetDerivedPropertyData(), dependencyData))
                {
                    dependencyData.AddUsedByProperty(derivedProperty);
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Updates dependencies using buffer reuse when possible.
    /// Reuses existing array if capacity is sufficient; allocates only when growing.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetRequiredProperties(ReadOnlySpan<RecordedDependency> recorded)
    {
        var newCount = recorded.Length;
        var currentArray = _requiredProperties;
        if (currentArray is null || currentArray.Length < newCount)
        {
            currentArray = new PropertyReference[newCount];
            _requiredProperties = currentArray;
        }

        for (var i = 0; i < newCount; i++)
        {
            currentArray[i] = recorded[i].Property;
        }

        if (currentArray.Length > newCount)
        {
            currentArray.AsSpan(newCount).Clear();
        }

        _requiredPropertyCount = newCount;
    }

    /// <summary>
    /// Clears all dependencies.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ClearRequiredProperties()
    {
        _requiredProperties = null;
        _requiredPropertyCount = 0;
    }

    /// <summary>
    /// Removes a dependency using swap-remove (no array allocation).
    /// Nulls out array when the last item is removed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool RemoveRequiredProperty(in PropertyReference property)
    {
        var required = _requiredProperties;
        var requiredCount = _requiredPropertyCount;
        if (required is null || requiredCount == 0)
        {
            return false;
        }

        var index = Array.IndexOf(required, property, 0, requiredCount);
        if (index < 0)
        {
            return false;
        }

        // Swap-remove: move the last item into the removed slot, decrement count.
        // No array allocation. Safe under lock.
        var lastIndex = requiredCount - 1;
        if (index < lastIndex)
        {
            required[index] = required[lastIndex];
        }

        required[lastIndex] = default;
        _requiredPropertyCount = lastIndex;

        if (lastIndex == 0)
        {
            _requiredProperties = null;
        }

        return true;
    }

    /// <summary>
    /// Adds a used-by property. Creates PropertyReferenceCollection on first call
    /// with the item pre-populated, avoiding a separate Add.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void AddUsedByProperty(in PropertyReference dependentProperty)
    {
        var usedByProperty = Volatile.Read(ref _usedByProperties);
        if (usedByProperty is not null)
        {
            usedByProperty.Add(dependentProperty);
            return;
        }

        // First used-by property: create the collection with the item already included instead of a separate Add.
        var created = new PropertyReferenceCollection(dependentProperty);
        var existing = Interlocked.CompareExchange(ref _usedByProperties, created, null);

        // Another thread created it first, add to theirs instead.
        existing?.Add(dependentProperty);
    }

    /// <summary>
    /// Removes a used-by property if the collection exists.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void RemoveUsedByProperty(in PropertyReference dependent)
    {
        _usedByProperties?.Remove(dependent);
    }
}
