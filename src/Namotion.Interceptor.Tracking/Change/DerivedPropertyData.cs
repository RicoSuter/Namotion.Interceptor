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
    /// Forward dependencies: Which properties this derived property depends on.
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
    /// Backward dependencies: Which derived properties depend on this property.
    /// Initialized lazily via Interlocked.CompareExchange for thread safety.
    /// </summary>
    private PropertyReferenceCollection? _usedByProperties;

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
    /// Read-only access to backward dependencies for public API.
    /// </summary>
    internal PropertyReferenceCollection? UsedByDependencies
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref _usedByProperties);
    }

    /// <summary>
    /// Whether this property has forward dependencies (has been evaluated as a derived property).
    /// </summary>
    internal bool HasRequiredProperties
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _requiredProperties is not null;
    }

    /// <summary>
    /// Returns the current forward dependencies as a read-only span.
    /// Returns empty span if no dependencies exist (allocation-free).
    /// </summary>
    internal ReadOnlySpan<PropertyReference> RequiredPropertiesSpan
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _requiredProperties.AsSpan(0, _requiredPropertyCount);
    }

    /// <summary>
    /// Returns backward dependencies as a read-only span (allocation-free).
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
    /// Detaches this property: sets IsAttached=false, cleans forward links (if derived),
    /// snapshots and clears backward links. Called under lock(this).
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
                requiredProperties[i].TryGetDerivedPropertyData()?.RemoveUsedByProperty(property);
            }

            ClearRequiredProperties();
            LastKnownValue = null;
        }

        // Case 2: snapshot backward deps, then clear. Snapshot is stable (copy-on-write).
        var snapshot = _usedByProperties?.ItemsArray ?? [];
        _usedByProperties = null;
        return snapshot;
    }
    
    /// <summary>
    /// Clears all forward dependencies.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ClearRequiredProperties()
    {
        _requiredProperties = null;
        _requiredPropertyCount = 0;
    }

    /// <summary>
    /// Updates forward (RequiredProperties) and backward (UsedByProperties) dependency links.
    /// Called under lock(this). Returns true if dependency set changed (caller should re-evaluate).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool UpdateDependencies(
        in PropertyReference derivedProperty,
        ReadOnlySpan<PropertyReference> recordedDependencies,
        DerivedPropertyRecorder recorder)
    {
        var previousRequiredProperties = RequiredPropertiesSpan;

        // Fast path: deps unchanged → no allocation.
        if (previousRequiredProperties.SequenceEqual(recordedDependencies))
        {
            recorder.ClearLastRecording();
            return false;
        }

        // Differential backward link update.
        // Both previousRequiredProperties and recordedDependencies spans are valid here
        // (array not yet modified, recorder not yet cleared).
        foreach (ref readonly var oldDependency in previousRequiredProperties)
        {
            if (!recordedDependencies.Contains(oldDependency))
            {
                oldDependency.TryGetDerivedPropertyData()?.RemoveUsedByProperty(derivedProperty);
            }
        }

        // Add backlinks for new deps. Lock depData to serialize with DetachProperty's IsAttached check.
        var hasSkippedDependencies = false;
        foreach (ref readonly var newDependency in recordedDependencies)
        {
            if (!previousRequiredProperties.Contains(newDependency))
            {
                var dependencyData = newDependency.GetDerivedPropertyData();
                lock (dependencyData)
                {
                    if (dependencyData.IsAttached)
                    {
                        dependencyData.AddUsedByProperty(derivedProperty);
                    }
                    else
                    {
                        hasSkippedDependencies = true;
                    }
                }
            }
        }

        if (!hasSkippedDependencies)
        {
            SetRequiredProperties(recordedDependencies);
            recorder.ClearLastRecording();
            return true;
        }

        // Rare path: a dependency was detaching concurrently. Re-check under lock to handle
        // re-attachment, then filter out still-detached deps from RequiredProperties.
        // Allocate owned copy to preserve previousRequiredProperties span for the final SequenceEqual check.
        var newItems = recordedDependencies.ToArray();
        recorder.ClearLastRecording();
        SetRequiredPropertiesFromArray(newItems);

        var liveCount = 0;
        for (var i = 0; i < newItems.Length; i++)
        {
            var dependencyData = newItems[i].TryGetDerivedPropertyData();
            if (dependencyData is null)
            {
                newItems[liveCount++] = newItems[i];
                continue;
            }

            lock (dependencyData)
            {
                if (dependencyData.IsAttached)
                {
                    dependencyData.AddUsedByProperty(derivedProperty);
                    newItems[liveCount++] = newItems[i];
                }
                else
                {
                    dependencyData.RemoveUsedByProperty(derivedProperty);
                }
            }
        }

        TrimRequiredProperties(liveCount);

        // If filtered result matches previous deps, nothing effectively changed.
        // Return false to prevent infinite stabilization loop.
        // previousRequiredProperties span is still valid (points to old array, replaced above).
        if (RequiredPropertiesSpan.SequenceEqual(previousRequiredProperties))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Updates forward dependencies using buffer reuse when possible.
    /// Reuses existing array if capacity is sufficient; allocates only when growing.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetRequiredProperties(ReadOnlySpan<PropertyReference> properties)
    {
        var newCount = properties.Length;
        var currentArray = _requiredProperties;
        if (currentArray is not null && currentArray.Length >= newCount)
        {
            // Reuse existing buffer — zero allocation.
            properties.CopyTo(currentArray);
            if (currentArray.Length > newCount)
            {
                currentArray.AsSpan(newCount).Clear();
            }
        }
        else
        {
            // Must grow: allocate new array.
            _requiredProperties = properties.ToArray();
        }

        _requiredPropertyCount = newCount;
    }

    /// <summary>
    /// Sets forward dependencies from an existing array (takes ownership).
    /// Count is set to array length.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetRequiredPropertiesFromArray(PropertyReference[] items)
    {
        _requiredProperties = items;
        _requiredPropertyCount = items.Length;
    }

    /// <summary>
    /// Trims forward dependencies to the specified live count after in-place filtering.
    /// Clears trailing entries. Nulls out array if liveCount is zero.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void TrimRequiredProperties(int liveCount)
    {
        if (liveCount == 0)
        {
            _requiredProperties = null;
            _requiredPropertyCount = 0;
        }
        else
        {
            Array.Clear(_requiredProperties!, liveCount, _requiredProperties!.Length - liveCount);
            _requiredPropertyCount = liveCount;
        }
    }

    /// <summary>
    /// Removes a forward dependency using swap-remove (no array allocation).
    /// Nulls out array when last item is removed.
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

        // Swap-remove: move last item into removed slot, decrement count.
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
    /// Adds a backward dependency. Creates PropertyReferenceCollection on first call
    /// with the item pre-populated, avoiding a separate CAS Add allocation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AddUsedByProperty(in PropertyReference dependentProperty)
    {
        var usedByProperty = Volatile.Read(ref _usedByProperties);
        if (usedByProperty is not null)
        {
            usedByProperty.Add(dependentProperty);
            return;
        }

        // First backward dependency: Create collection with item already included (one allocation instead of two).
        var created = new PropertyReferenceCollection(dependentProperty);
        var existing = Interlocked.CompareExchange(ref _usedByProperties, created, null);
      
        // Another thread created it first, add to theirs instead.
        existing?.Add(dependentProperty);
    }

    /// <summary>
    /// Removes a backward dependency if the collection exists.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RemoveUsedByProperty(in PropertyReference dependent)
    {
        _usedByProperties?.Remove(dependent);
    }
}
