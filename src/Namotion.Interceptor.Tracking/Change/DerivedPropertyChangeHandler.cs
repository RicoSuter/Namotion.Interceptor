using System.Runtime.CompilerServices;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Handles derived property tracking and automatic recalculation using dependency recording.
/// Requires LifecycleInterceptor to be added after this interceptor.
/// </summary>
/// <remarks>
/// Lock ordering / deadlock safety:
/// Locks are always acquired on per-property <see cref="DerivedPropertyData"/> objects.
/// <para>
/// <see cref="RecalculateDerivedProperty"/> and <see cref="StoreRecordedTouchedProperties"/>
/// nest locks in derived → dependency direction: lock(D_data) → lock(X_data).
/// A deadlock would require a cycle, which would imply a circular dependency in the property
/// graph — but circular getter dependencies cause infinite recursion before any lock is reached,
/// so the graph is always a DAG and deadlock is impossible.
/// </para>
/// <para>
/// <see cref="DetachProperty"/> uses a single lock(data) for all local cleanup (IsAttached, forward deps,
/// backward snapshot), then acquires lock(derivedData) sequentially for RequiredProperties cleanup.
/// Because DetachProperty never holds two locks simultaneously, it cannot participate in a lock cycle.
/// </para>
/// </remarks>
[RunsBefore(typeof(LifecycleInterceptor))]
public class DerivedPropertyChangeHandler : IReadInterceptor, IWriteInterceptor, IPropertyLifecycleHandler
{
    // Lock-free dependency tracking with an allocation-free steady state.
    // Records property reads → builds dependency graph → recalculates dependents on write.

    [ThreadStatic]
    private static DerivedPropertyRecorder? _recorder;

    private static readonly Action<IInterceptorSubject, object?> NoOpWriteDelegate = static (_, _) => { };

    // Global write-detection counter for concurrent-write checks in AttachProperty and
    // RecalculateDerivedProperty. Static so writes from any context are detected, even when
    // dependencies span contexts (e.g., via context inheritance).
    // Incremented atomically via Interlocked.Increment on every property write, ensuring each
    // write produces a unique counter value (no lost increments from concurrent writers).
    // Interlocked.Increment provides a full fence, pairing with Volatile.Read's acquire semantics
    // to guarantee that committed property values are visible when the counter change is observed.
    // False positives (unrelated writes) only affect AttachProperty and RecalculateDerivedProperty
    // when deps change — the steady-state write path never checks the generation.
    private static int _writeGeneration;

    /// <inheritdoc />
    public void AttachProperty(SubjectPropertyLifecycleChange change)
    {
        var metadata = change.Property.Metadata;

        // For derived properties, GetDerivedPropertyData creates data if needed (first attach).
        // For source properties, TryGet returns null on first attach (data created later by
        // StoreRecordedTouchedProperties when a derived property first depends on it).
        var data = metadata.IsDerived
            ? change.Property.GetDerivedPropertyData()
            : change.Property.TryGetDerivedPropertyData();

        if (data is null)
        {
            return;
        }

        lock (data)
        {
            // Reset IsAttached for re-attachment after DetachProperty cleared it.
            // On first attach the field default (true) is redundant but harmless.
            data.IsAttached = true;

            if (metadata.IsDerived)
            {
                // Single evaluation, with generation-based concurrent-write detection.
                // On the common path (single-threaded construction), _writeGeneration is
                // unchanged → zero extra getter evaluations (vs. the old unconditional loop).
                // If a concurrent write IS detected (rare), fall back to the stabilization
                // loop for full correctness (same as RecalculateDerivedProperty).
                object? result;
                try
                {
                    var generationBefore = Volatile.Read(ref _writeGeneration);

                    StartRecordingTouchedProperties();
                    result = metadata.GetValue?.Invoke(change.Subject);
                    StoreRecordedTouchedProperties(change.Property, data);

                    if (Volatile.Read(ref _writeGeneration) != generationBefore)
                    {
                        bool dependenciesChanged;
                        do
                        {
                            StartRecordingTouchedProperties();
                            result = metadata.GetValue?.Invoke(change.Subject);
                            dependenciesChanged = StoreRecordedTouchedProperties(change.Property, data);
                        }
                        while (dependenciesChanged);
                    }
                }
                finally
                {
                    DiscardActiveRecording();
                }

                data.LastKnownValue = result;
                change.Property.SetWriteTimestampUtcTicks(SubjectChangeContext.Current.ChangedTimestampUtcTicks);
            }
        }
    }

    /// <inheritdoc />
    public void DetachProperty(SubjectPropertyLifecycleChange change)
    {
        var property = change.Property;

        var data = property.GetDerivedPropertyData();

        // Single lock handles both forward (Case 1) and backward (Case 2) cleanup.
        // Case 1 (derived only): Remove this property from its dependencies' UsedByProperties.
        // Case 2 (all properties): Snapshot and clear UsedByProperties, then clean dependents' RequiredProperties.
        // Snapshot under lock serializes with StoreRecordedTouchedProperties' backlink Add (which also locks depData).
        // This ensures: either the backlink was added before our snapshot (we see it and clean up),
        // or StoreRecordedTouchedProperties acquires the lock after us and sees IsAttached=false (skips Add).
        // Release lock before processing Case 2 snapshot to avoid nesting with lock(derivedData) → no deadlock.
        PropertyReference[] usedBySnapshot;
        lock (data)
        {
            data.IsAttached = false;

            // Case 1: Derived property — remove forward dependencies.
            if (property.Metadata.IsDerived)
            {
                var requiredProperties = data.RequiredProperties;
                if (requiredProperties is not null)
                {
                    foreach (var dependency in requiredProperties.AsSpan())
                    {
                        dependency.TryGetDerivedPropertyData()?.UsedByProperties?.Remove(property);
                    }

                    data.RequiredProperties = null;
                }

                data.LastKnownValue = null;
            }

            // Case 2: Snapshot backward dependencies for cleanup outside lock.
            // ItemsArray returns a stable copy-on-write snapshot, safe to use after releasing the lock.
            usedBySnapshot = data.UsedByProperties?.ItemsArray ?? [];
            data.UsedByProperties = null;
        }

        // Case 2: Remove this property from each dependent's RequiredProperties.
        foreach (ref readonly var derivedProperty in usedBySnapshot.AsSpan())
        {
            var derivedData = derivedProperty.TryGetDerivedPropertyData();
            if (derivedData is null)
            {
                continue;
            }

            lock (derivedData)
            {
                var required = derivedData.RequiredProperties;
                if (required is null)
                {
                    continue;
                }

                var index = Array.IndexOf(required, property);
                if (index < 0)
                {
                    continue;
                }

                derivedData.RequiredProperties = required.Length == 1
                    ? null
                    : DerivedPropertyDependencies.RemoveAt(required, index);
            }
        }
    }

    /// <inheritdoc />
    public TProperty ReadProperty<TProperty>(ref PropertyReadContext context, ReadInterceptionDelegate<TProperty> next)
    {
        var result = next(ref context);
        
        // ReSharper disable InconsistentlySynchronizedField (thread static)
        if (_recorder?.IsRecording == true)
        {
            var property = context.Property;
            _recorder.TouchProperty(ref property);
        }
        // ReSharper restore InconsistentlySynchronizedField

        return result;
    }

    /// <inheritdoc />
    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        next(ref context);

        // Signal write for AttachProperty's concurrent-write detection.
        // Placed before TryGetDerivedPropertyData so writes to not-yet-tracked properties
        // (which may become dependencies during a concurrent AttachProperty) are also detected.
        Interlocked.Increment(ref _writeGeneration);

        // Fast path: skip all post-write processing for properties without tracking data.
        var data = context.Property.TryGetDerivedPropertyData();
        if (data is null)
        {
            return;
        }

        // If this property is itself a derived property with a setter, recalculate it.
        // The setter modifies internal state, but the actual value is computed by the getter.
        // We need to: 1) re-record dependencies, 2) fire change notification with correct value.
        // RequiredProperties is only set for derived properties (during StoreRecordedTouchedProperties).
        if (data.RequiredProperties is not null && context.Property.Metadata.SetValue is not null)
        {
            var currentTimestampUtcTicks = SubjectChangeContext.Current.ChangedTimestampUtcTicks;
            var property = context.Property;
            RecalculateDerivedProperty(ref property, currentTimestampUtcTicks);
        }

        var usedByProperties = Volatile.Read(ref data.UsedByProperties);
        if (usedByProperties is not null && usedByProperties.Count > 0)
        {
            // Skip dependent recalculation during transaction capture.
            // Derived-with-setter properties bypass the transaction interceptor (IsDerived check),
            // so this guard suppresses cascading recalculations until commit replay.
            if (SubjectTransaction.HasActiveTransaction &&
                SubjectTransaction.Current is { IsCommitting: false })
            {
                return;
            }

            var usedByPropertiesItems = usedByProperties.Items;
            var timestampUtcTicks = SubjectChangeContext.Current.ChangedTimestampUtcTicks;
            for (var i = 0; i < usedByPropertiesItems.Length; i++)
            {
                var dependent = usedByPropertiesItems[i];
                if (dependent == context.Property)
                {
                    continue; // Skip self-references (rare edge case)
                }

                RecalculateDerivedProperty(ref dependent, timestampUtcTicks);
            }
        }
    }

    /// <summary>
    /// Recalculates a derived property when one of its dependencies changes.
    /// Locks on the per-property DerivedPropertyData to serialize concurrent recalculations
    /// of the same derived property, ensuring dependencies and value stay consistent.
    /// </summary>
    private static void RecalculateDerivedProperty(ref PropertyReference derivedProperty, long timestampUtcTicks)
    {
        // TODO(perf): Avoid boxing when possible (use TProperty generic parameter?)

        var data = derivedProperty.GetDerivedPropertyData();
        lock (data)
        {
            // Re-entrancy guard: when a derived-with-setter property is recalculated,
            // SetPropertyValueWithInterception re-enters WriteProperty which would call
            // RecalculateDerivedProperty again for the same property. The lock is re-entrant
            // (same thread), so without this guard the recursion is infinite.
            // Cross-thread callers block on the lock and see IsRecalculating=false after release.
            if (data.IsRecalculating)
            {
                return;
            }

            if (!data.IsAttached)
            {
                return;
            }

            data.IsRecalculating = true;
            try
            {
                var oldValue = data.LastKnownValue;
                var generationBefore = Volatile.Read(ref _writeGeneration);

                // Single evaluation, with generation-based concurrent-write detection.
                // If deps changed (conditional branch or cross-subject swap) but no
                // concurrent write occurred, the recorded deps and value are already
                // correct — skip re-evaluation. If a concurrent write IS detected,
                // fall back to the stabilization loop for full correctness.
                StartRecordingTouchedProperties();
                var newValue = derivedProperty.Metadata.GetValue?.Invoke(derivedProperty.Subject);
                var dependenciesChanged = StoreRecordedTouchedProperties(derivedProperty, data);

                if (dependenciesChanged && Volatile.Read(ref _writeGeneration) != generationBefore)
                {
                    do
                    {
                        StartRecordingTouchedProperties();
                        newValue = derivedProperty.Metadata.GetValue?.Invoke(derivedProperty.Subject);
                        dependenciesChanged = StoreRecordedTouchedProperties(derivedProperty, data);
                    }
                    while (dependenciesChanged);
                }

                data.LastKnownValue = newValue;
                derivedProperty.SetWriteTimestampUtcTicks(timestampUtcTicks);

                // Fire change notification with known old value (zero-alloc, no ThreadStatic).
                using (SubjectChangeContext.WithSource(null))
                {
                    derivedProperty.SetPropertyValueWithInterception(newValue, oldValue, NoOpWriteDelegate);
                }

                if (derivedProperty.Subject is IRaisePropertyChanged raiser)
                {
                    raiser.RaisePropertyChanged(derivedProperty.Metadata.Name);
                }
            }
            finally
            {
                DiscardActiveRecording();
                data.IsRecalculating = false;
            }
        }
    }

    /// <summary>
    /// Starts recording property accesses for dependency tracking.
    /// </summary>
    private static void StartRecordingTouchedProperties()
    {
        _recorder ??= new DerivedPropertyRecorder();
        _recorder.StartRecording();
    }

    /// <summary>
    /// Discards an active recording if the getter threw before StoreRecordedTouchedProperties could run.
    /// Called in finally blocks to prevent recorder depth corruption.
    /// Zero overhead on the happy path (recording is already finished).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DiscardActiveRecording()
    {
        if (_recorder is null)
        {
            return;
        }

        if (_recorder.IsRecording)
        {
            _recorder.FinishRecording();
        }

        // Always clear, even if recording was already finished by StoreRecordedTouchedProperties.
        // Handles the case where FinishRecording succeeded but ClearLastRecording didn't run
        // (exception between them). No-op if already cleared (Count == 0).
        _recorder.ClearLastRecording();
    }

    /// <summary>
    /// Updates forward (required) and backward (usedBy) dependencies.
    /// Called under lock(data) for the derived property, so RequiredProperties access is serialized.
    /// Backward link additions lock the dependency's data to serialize with DetachProperty.
    /// </summary>
    /// <returns>True if the dependency set changed (caller should re-evaluate); false if unchanged.</returns>
    private static bool StoreRecordedTouchedProperties(PropertyReference derivedProperty, DerivedPropertyData data)
    {
        var recordedDependencies = _recorder!.FinishRecording();
        var previousItems = data.RequiredProperties.AsSpan();

        // Steady-state fast path: no allocation if dependencies unchanged.
        if (previousItems.SequenceEqual(recordedDependencies))
        {
            _recorder.ClearLastRecording();
            return false;
        }

        // Copy to array and release recorder buffer immediately.
        // After ClearLastRecording(), the recordedDependencies span is invalidated;
        // all subsequent code uses newItems instead.
        var newItems = recordedDependencies.ToArray();
        _recorder.ClearLastRecording();

        // Replace forward dependencies (simple assignment under lock).
        data.RequiredProperties = newItems;

        // Update backward links differentially.
        // Remove this derived property from old dependencies no longer used.
        ReadOnlySpan<PropertyReference> newDeps = newItems;
        foreach (ref readonly var oldDependency in previousItems)
        {
            if (!newDeps.Contains(oldDependency))
            {
                oldDependency.TryGetDerivedPropertyData()?.UsedByProperties?.Remove(derivedProperty);
            }
        }

        // Add this derived property to new dependencies not previously tracked.
        // Lock depData to serialize the IsAttached check + Add with DetachProperty's
        // IsAttached=false + snapshot (both under the same lock). This guarantees:
        // - If DetachProperty wins the lock: sets IsAttached=false, takes snapshot. We see false → skip.
        // - If we win the lock: IsAttached=true, Add succeeds. DetachProperty's snapshot includes us → cleans up.
        var hasSkippedDependencies = false;
        foreach (ref readonly var newDependency in newDeps)
        {
            if (!previousItems.Contains(newDependency))
            {
                var depData = newDependency.GetDerivedPropertyData();
                lock (depData)
                {
                    if (depData.IsAttached)
                    {
                        depData.GetOrCreateUsedByProperties().Add(derivedProperty);
                    }
                    else
                    {
                        hasSkippedDependencies = true;
                    }
                }
            }
        }

        // When a backlink Add was skipped (dependency detaching), remove that dependency
        // from RequiredProperties to prevent forward reference leaks (memory) and stale deps.
        // Re-check IsAttached under lock to handle concurrent re-attachment: if a dependency
        // was detached during the backlink loop but re-attached before we get here, the locked
        // check sees IsAttached=true and adds the backward link (idempotent Add), ensuring
        // forward and backward links stay consistent.
        // Only runs in the rare concurrent detach race — zero overhead on the normal path.
        if (hasSkippedDependencies)
        {
            var liveCount = 0;
            for (var i = 0; i < newItems.Length; i++)
            {
                var depData = newItems[i].TryGetDerivedPropertyData();
                if (depData is null)
                {
                    newItems[liveCount++] = newItems[i];
                    continue;
                }

                lock (depData)
                {
                    if (depData.IsAttached)
                    {
                        // Dependency is (still or again) attached. Ensure backward link exists.
                        // Idempotent for deps that were successfully added in the backlink loop.
                        depData.GetOrCreateUsedByProperties().Add(derivedProperty);
                        newItems[liveCount++] = newItems[i];
                    }
                    else
                    {
                        // Still detached — remove from RequiredProperties and clean backward link.
                        depData.UsedByProperties?.Remove(derivedProperty);
                    }
                }
            }

            data.RequiredProperties = liveCount == 0
                ? null
                : newItems.AsSpan(0, liveCount).ToArray();

            // If filtered result matches previous deps, nothing effectively changed —
            // return false to prevent the stabilization loop from re-evaluating infinitely
            // (getter keeps reading the detached dep, we keep filtering it out).
            if (data.RequiredProperties.AsSpan().SequenceEqual(previousItems))
            {
                return false;
            }
        }

        return true;
    }
}
