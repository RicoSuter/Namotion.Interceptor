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
/// Deadlock safety: locks are acquired on per-property <see cref="DerivedPropertyData"/> objects.
/// Nesting follows derived → dependency direction (DAG), so cycles are impossible.
/// <see cref="DetachProperty"/> never holds two locks simultaneously.
/// See docs/design/tracking-derived-properties.md for full concurrency analysis.
/// </remarks>
[RunsBefore(typeof(LifecycleInterceptor))]
public class DerivedPropertyChangeHandler : IReadInterceptor, IWriteInterceptor, IPropertyLifecycleHandler
{
    [ThreadStatic]
    private static DerivedPropertyRecorder? _recorder;

    private static readonly Action<IInterceptorSubject, object?> NoOpWriteDelegate = static (_, _) => { };

    // Global counter incremented on every write (Interlocked.Increment, full fence).
    // Paired with Volatile.Read in AttachProperty/RecalculateDerivedProperty to detect
    // concurrent writes during getter evaluation. Static so cross-context writes are visible.
    // False positives only trigger re-evaluation when deps actually changed.
    private static int _writeGeneration;

    /// <inheritdoc />
    public void AttachProperty(SubjectPropertyLifecycleChange change)
    {
        var metadata = change.Property.Metadata;

        // Derived: create data if needed. Source: only get existing (created by Store when first depended on).
        var data = metadata.IsDerived
            ? change.Property.GetDerivedPropertyData()
            : change.Property.TryGetDerivedPropertyData();

        if (data is null)
        {
            return;
        }

        lock (data)
        {
            data.IsAttached = true;

            if (metadata.IsDerived)
            {
                // Evaluate getter once, re-evaluate only if a concurrent write is detected.
                object? result;
                try
                {
                    var generationBefore = Volatile.Read(ref _writeGeneration);

                    StartRecordingTouchedProperties();
                    result = metadata.GetValue?.Invoke(change.Subject);
                    StoreRecordedTouchedProperties(change.Property, data);

                    if (Volatile.Read(ref _writeGeneration) != generationBefore)
                    {
                        // Concurrent write detected — stabilization loop.
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

        // Skip properties without tracking data (never participated in dependency graph).
        var data = property.TryGetDerivedPropertyData();
        if (data is null)
        {
            return;
        }

        // Single lock: set IsAttached=false, clean forward links (Case 1), snapshot backward links (Case 2).
        // Lock serializes with StoreRecordedTouchedProperties' backlink Add on the same depData.
        PropertyReference[] usedBySnapshot;
        lock (data)
        {
            data.IsAttached = false;

            // Case 1 (derived only): remove this property from each dependency's UsedByProperties.
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

            // Case 2: snapshot backward deps, then clear. Snapshot is stable (copy-on-write).
            usedBySnapshot = data.UsedByProperties?.ItemsArray ?? [];
            data.UsedByProperties = null;
        }

        // Case 2: remove this property from each dependent's RequiredProperties (outside lock).
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

        // Signal write before TryGet so writes to not-yet-tracked properties are also detected.
        Interlocked.Increment(ref _writeGeneration);

        var data = context.Property.TryGetDerivedPropertyData();
        if (data is null)
        {
            return;
        }

        // Derived-with-setter: setter modifies state, but value comes from the getter → recalculate.
        if (data.RequiredProperties is not null && context.Property.Metadata.SetValue is not null)
        {
            var currentTimestampUtcTicks = SubjectChangeContext.Current.ChangedTimestampUtcTicks;
            var property = context.Property;
            RecalculateDerivedProperty(ref property, currentTimestampUtcTicks);
        }

        var usedByProperties = Volatile.Read(ref data.UsedByProperties);
        if (usedByProperties is not null && usedByProperties.Count > 0)
        {
            // Suppress cascading recalculations during transaction capture (replayed on commit).
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
    /// Recalculates a derived property: re-evaluates getter, updates deps, fires change notification.
    /// Locks per-property data to serialize concurrent recalculations.
    /// </summary>
    private static void RecalculateDerivedProperty(ref PropertyReference derivedProperty, long timestampUtcTicks)
    {
        // TODO(perf): Avoid boxing when possible (use TProperty generic parameter?)

        var data = derivedProperty.GetDerivedPropertyData();
        lock (data)
        {
            // Re-entrancy guard: derived-with-setter → SetPropertyValueWithInterception → WriteProperty
            // → RecalculateDerivedProperty. Lock is re-entrant (same thread), so this flag is needed.
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

                StartRecordingTouchedProperties();
                var newValue = derivedProperty.Metadata.GetValue?.Invoke(derivedProperty.Subject);
                var dependenciesChanged = StoreRecordedTouchedProperties(derivedProperty, data);

                // Stabilization loop: only when deps changed AND a concurrent write was detected.
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

                // Fire change notification (null source = internal recalculation, not external).
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

    private static void StartRecordingTouchedProperties()
    {
        _recorder ??= new DerivedPropertyRecorder();
        _recorder.StartRecording();
    }

    /// <summary>
    /// Cleans up recorder state if getter threw before StoreRecordedTouchedProperties ran.
    /// No-op on the happy path (recording already finished and cleared).
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

        // Always clear to prevent thread-static from holding refs to detached subjects.
        _recorder.ClearLastRecording();
    }

    /// <summary>
    /// Updates forward (RequiredProperties) and backward (UsedByProperties) dependency links.
    /// Called under lock(data). Returns true if dependency set changed (caller should re-evaluate).
    /// </summary>
    private static bool StoreRecordedTouchedProperties(PropertyReference derivedProperty, DerivedPropertyData data)
    {
        var recordedDependencies = _recorder!.FinishRecording();
        var previousItems = data.RequiredProperties.AsSpan();

        // Fast path: deps unchanged → no allocation.
        if (previousItems.SequenceEqual(recordedDependencies))
        {
            _recorder.ClearLastRecording();
            return false;
        }

        // Copy recorded deps to owned array, then release recorder buffer.
        var newItems = recordedDependencies.ToArray();
        _recorder.ClearLastRecording();

        data.RequiredProperties = newItems;

        // Differential backward link update.
        ReadOnlySpan<PropertyReference> newDeps = newItems;
        foreach (ref readonly var oldDependency in previousItems)
        {
            if (!newDeps.Contains(oldDependency))
            {
                oldDependency.TryGetDerivedPropertyData()?.UsedByProperties?.Remove(derivedProperty);
            }
        }

        // Add backlinks for new deps. Lock depData to serialize with DetachProperty's IsAttached check.
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

        // Rare path: a dependency was detaching concurrently. Re-check under lock to handle
        // re-attachment, then filter out still-detached deps from RequiredProperties.
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
                        depData.GetOrCreateUsedByProperties().Add(derivedProperty);
                        newItems[liveCount++] = newItems[i];
                    }
                    else
                    {
                        depData.UsedByProperties?.Remove(derivedProperty);
                    }
                }
            }

            data.RequiredProperties = liveCount == 0
                ? null
                : newItems.AsSpan(0, liveCount).ToArray();

            // If filtered result matches previous deps, nothing effectively changed.
            // Return false to prevent infinite stabilization loop.
            if (data.RequiredProperties.AsSpan().SequenceEqual(previousItems))
            {
                return false;
            }
        }

        return true;
    }
}
