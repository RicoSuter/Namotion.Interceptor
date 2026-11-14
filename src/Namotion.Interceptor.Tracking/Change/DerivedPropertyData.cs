using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Consolidated per-property data for derived property tracking.
/// Created once per (subject, property) and never removed or replaced. Stored in Subject.Data under a short key
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
    /// The data of each dependency, at the index of its <see cref="_requiredProperties"/> entry. Written under
    /// lock(this); read without the lock by an evaluation of this property (<see cref="RequiredData"/>), which
    /// checks each entry's <see cref="Property"/> before it uses it.
    /// </summary>
    private DerivedPropertyData?[]? _requiredData;

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

    // Ownership state, see "Ownership Protocol" in docs/design/tracking-derived-properties.md. Read and written only
    // under lock(this) unless noted.

    /// <summary>The token of the evaluation that owns this derived property (a recalculation or an attach), or 0.</summary>
    internal long OwnerToken;

    /// <summary>The token of the attach in progress, or 0; a newer attach or a detach replaces it.</summary>
    internal long AttachToken;

    /// <summary>
    /// Incremented for every committed value and every detach. Read via Volatile.Read outside the lock to skip a
    /// stale publication: if the sequence has advanced, a newer commit or a detach happened.
    /// </summary>
    internal long RecalculationSequence;

    /// <summary>A recalculation was requested while the property is owned or an attach is in progress.</summary>
    internal bool RecalculationNeeded;

    /// <summary>A pending request came from another thread than the owner's, or while nobody owned the property.</summary>
    internal bool HasForeignRequest;

    /// <summary>The managed thread id of the owner set with <see cref="OwnerToken"/>.</summary>
    internal int OwnerThreadId;

    /// <summary>The raw changed timestamp of the newest pending request, or 0.</summary>
    internal long PendingRawTimestamp;

    /// <summary>The derived property is attached. Not read for source properties.</summary>
    internal bool IsAttached;

    /// <summary><see cref="LastKnownValue"/> holds a committed value of the current attachment.</summary>
    internal bool HasValue;

    /// <summary>The last evaluation threw, so the first success afterwards is published.</summary>
    internal bool IsFaulted;

    /// <summary>
    /// True if this data belongs to a derived property (vs. a source property whose data exists only
    /// because a derived depends on it). Lets the write observer identify derived-with-setter writes
    /// without touching metadata on the hot path.
    /// </summary>
    /// <remarks>
    /// Written via <c>Volatile.Write</c> by the attach, read via <c>Volatile.Read</c> by the write observer and
    /// a recalculation request. Write-once; never reset (metadata.IsDerived is immutable).
    /// </remarks>
    internal bool IsDerived;

    private static long _lastToken;

    private DerivedPropertyData(PropertyReference property)
    {
        Property = property;
    }

    /// <summary>The property this data belongs to.</summary>
    internal PropertyReference Property { get; }

    /// <summary>
    /// Creates the data of <paramref name="property"/> and raises the gate of its subject's type first, so the
    /// gate is raised before the data can be found.
    /// </summary>
    internal static DerivedPropertyData Create(PropertyReference property)
    {
        DerivedPropertyTypeGate.Of(property.Subject).Raise();
        return new DerivedPropertyData(property);
    }

    /// <summary>Returns a new, process-wide unique, nonzero owner or attach token.</summary>
    internal static long NewToken() => Interlocked.Increment(ref _lastToken);

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
    /// The dependency data of the last committed links, in read order, or null. Read without the lock: the array
    /// can change while it is read, so an entry is only valid for the property it names.
    /// </summary>
    internal DerivedPropertyData?[]? RequiredData
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref _requiredData);
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
    /// Removes this derived property from the used-by set of every dependency and clears its dependencies.
    /// Called under lock(this).
    /// </summary>
    internal void RemoveAllLinks(in PropertyReference derivedProperty)
    {
        foreach (ref readonly var dependency in RequiredPropertiesSpan)
        {
            dependency.TryGetDerivedPropertyData()?.RemoveUsedByProperty(derivedProperty);
        }

        ClearRequiredProperties();
    }

    /// <summary>
    /// Updates dependencies (RequiredProperties) and used-by properties (UsedByProperties) to the recorded set.
    /// Called under lock(this).
    /// </summary>
    internal void UpdateDependencies(in PropertyReference derivedProperty, ReadOnlySpan<RecordedDependency> recorded)
    {
        var previous = RequiredPropertiesSpan;
        if (previous.Length == recorded.Length && IsSameOrder(previous, recorded))
        {
            return;
        }

        foreach (ref readonly var old in previous)
        {
            if (IndexOf(recorded, old) < 0)
            {
                old.TryGetDerivedPropertyData()?.RemoveUsedByProperty(derivedProperty);
            }
        }

        foreach (ref readonly var dependency in recorded)
        {
            if (previous.IndexOf(dependency.Property) < 0)
            {
                dependency.Property.GetDerivedPropertyData().AddUsedByProperty(derivedProperty);
            }
        }

        SetRequiredProperties(recorded);
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

    /// <summary>
    /// Updates dependencies using buffer reuse when possible.
    /// Reuses existing array if capacity is sufficient; allocates only when growing.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetRequiredProperties(ReadOnlySpan<RecordedDependency> recorded)
    {
        var newCount = recorded.Length;
        var currentArray = _requiredProperties;
        var currentData = _requiredData;
        if (currentArray is null || currentData is null || currentArray.Length < newCount)
        {
            currentArray = new PropertyReference[newCount];
            currentData = new DerivedPropertyData?[newCount];
            _requiredProperties = currentArray;
            Volatile.Write(ref _requiredData, currentData);
        }

        for (var i = 0; i < newCount; i++)
        {
            currentArray[i] = recorded[i].Property;
            currentData[i] = recorded[i].Data;
        }

        if (currentArray.Length > newCount)
        {
            currentArray.AsSpan(newCount).Clear();
            currentData.AsSpan(newCount).Clear();
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
        _requiredData = null;
        _requiredPropertyCount = 0;
    }

    /// <summary>
    /// Adds a used-by property. Creates PropertyReferenceCollection on first call
    /// with the item pre-populated, avoiding a separate Add.
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
    private void RemoveUsedByProperty(in PropertyReference dependent)
    {
        _usedByProperties?.Remove(dependent);
    }
}
