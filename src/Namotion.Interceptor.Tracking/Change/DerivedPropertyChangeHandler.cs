using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Handles derived property tracking and automatic recalculation using dependency recording.
/// Requires LifecycleInterceptor to be added after this interceptor.
/// </summary>
[RunsBefore(typeof(LifecycleInterceptor))]
public class DerivedPropertyChangeHandler : IReadInterceptor, IWriteInterceptor, IPropertyLifecycleHandler
{
    // - Records property reads during derived property evaluation
    // - Builds dependency graph: derived property → source properties
    // - When source property changes, recalculates all dependent derived properties
    // - Uses optimistic concurrency control to handle concurrent updates safely

    // Thread Safety: Lock-free with allocation-free steady state. Handles concurrent writes via merge mode.

    [ThreadStatic]
    private static DerivedPropertyRecorder? _recorder;

    // Thread-local storage for derived property recalculation to avoid closure allocations.
    // These fields allow using static delegates instead of capturing closures.
    [ThreadStatic]
    private static object? _threadLocalOldValue;

    // Deferred Case 2 removal buffer: During write-triggered detach, Case 2 removals
    // (removing detached source from derived properties' RequiredProperties) are buffered
    // here instead of executing immediately. After recalculation, the buffer is flushed.
    // For targets that were recalculated, TryReplace already replaced RequiredProperties,
    // so Remove() finds nothing (no allocation). For targets not recalculated, Remove()
    // performs the normal CAS cleanup. This turns N costly CAS+allocation operations into
    // N cheap "not found" lookups in the common case.
    [ThreadStatic]
    private static int _writePropertyDepth;

    [ThreadStatic]
    private static List<(DerivedPropertyDependencies target, PropertyReference source)>? _pendingRemovals;

    private static readonly Func<IInterceptorSubject, object?> GetOldValueDelegate = static _ => _threadLocalOldValue;
    private static readonly Action<IInterceptorSubject, object?> NoOpWriteDelegate = static (_, _) => { };

    /// <inheritdoc />
    public void AttachProperty(SubjectPropertyLifecycleChange change)
    {
        var metadata = change.Property.Metadata;
        if (metadata.IsDerived)
        {
            // Get consolidated data once for LastKnownValue + StoreRecordedTouchedProperties
            var data = change.Property.GetDerivedPropertyData();

            StartRecordingTouchedProperties();

            var result = metadata.GetValue?.Invoke(change.Subject);
            data.LastKnownValue = result;
            change.Property.SetWriteTimestampUtcTicks(SubjectChangeContext.Current.ChangedTimestampUtcTicks);

            StoreRecordedTouchedProperties(change.Property, data);
        }
    }

    /// <inheritdoc />
    public void DetachProperty(SubjectPropertyLifecycleChange change)
    {
        var property = change.Property;

        // Single lookup for consolidated data (covers both Case 1 and Case 2)
        var data = property.TryGetDerivedPropertyData();
        if (data is null)
        {
            return;
        }

        // Case 1: This is a derived property being detached.
        // Remove it from all its dependencies' UsedByProperties to break backward refs.
        if (property.Metadata.IsDerived)
        {
            var requiredProperties = data.RequiredProperties;
            if (requiredProperties is not null)
            {
                foreach (ref readonly var dependency in requiredProperties.Items)
                {
                    // Use TryGet to avoid allocating on dependencies that have no UsedByProperties
                    dependency.TryGetDerivedPropertyData()?.UsedByProperties?.Remove(property);
                }
            }
        }

        // Case 2: This property (source or derived) might be used by derived properties on OTHER subjects.
        // Remove this property from those derived properties' RequiredProperties to prevent memory leaks.
        var usedByProperties = data.UsedByProperties;
        if (usedByProperties is null || usedByProperties.Count == 0)
        {
            return;
        }

        if (_writePropertyDepth > 0)
        {
            // Inside WriteProperty: defer removals. After recalculation, TryReplace will have
            // already replaced RequiredProperties for recalculated targets, so the deferred
            // Remove() calls will find nothing and return immediately (no CAS, no allocation).
            // For non-recalculated targets, Remove() performs normal cleanup.
            _pendingRemovals ??= new(4);
            foreach (ref readonly var derivedProperty in usedByProperties.Items)
            {
                var derivedData = derivedProperty.TryGetDerivedPropertyData();
                var requiredProperties = derivedData?.RequiredProperties;
                if (requiredProperties is not null)
                {
                    _pendingRemovals.Add((requiredProperties, property));
                }
            }
        }
        else
        {
            // Standalone detach (not inside WriteProperty): immediate cleanup.
            foreach (ref readonly var derivedProperty in usedByProperties.Items)
            {
                derivedProperty.TryGetDerivedPropertyData()?.RequiredProperties?.Remove(property);
            }
        }
    }

    /// <inheritdoc />
    public TProperty ReadProperty<TProperty>(ref PropertyReadContext context, ReadInterceptionDelegate<TProperty> next)
    {
        var result = next(ref context);

        if (_recorder?.IsRecording == true)
        {
            var property = context.Property;
            _recorder.TouchProperty(ref property);
        }

        return result;
    }

    /// <inheritdoc />
    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        _writePropertyDepth++;
        try
        {
            next(ref context);
        }
        finally
        {
            _writePropertyDepth--;
        }

        // If this property is itself a derived property with a setter, recalculate it.
        // This handles the case where SetValue is called directly on a derived property -
        // the setter modifies internal state, but the actual value is computed by the getter.
        // We need to: 1) re-record dependencies, 2) fire change notification with correct value.
        // Performance: Two boolean checks for common case, extra getter call only for
        // the rare case of derived properties with setters.
        var metadata = context.Property.Metadata;
        if (metadata is { IsDerived: true, SetValue: not null })
        {
            var currentTimestampUtcTicks = SubjectChangeContext.Current.ChangedTimestampUtcTicks;
            var property = context.Property;
            RecalculateDerivedProperty(ref property, currentTimestampUtcTicks);
        }

        // Single lookup for consolidated data (short key "ni.dpd")
        // Use TryGet to avoid allocating DerivedPropertyData for properties with no dependents
        var usedByProperties = context.Property.TryGetDerivedPropertyData()?.UsedByProperties;
        if (usedByProperties is null || usedByProperties.Count == 0)
        {
            FlushPendingRemovals();
            return;
        }

        var usedByPropertiesItems = usedByProperties.Items;

        // Skip dependent recalculation during transaction capture.
        // The [RunsBefore] ordering (Transaction before Derived) prevents non-derived writes
        // from reaching here during capture. However, derived-with-setter properties bypass
        // the transaction interceptor (IsDerived check), so this guard is still needed to
        // suppress cascading recalculations until commit replay.
        if (SubjectTransaction.HasActiveTransaction &&
            SubjectTransaction.Current is { IsCommitting: false })
        {
            FlushPendingRemovals();
            return;
        }

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

        FlushPendingRemovals();
    }

    /// <summary>
    /// Recalculates a derived property when one of its dependencies changes.
    /// Records new dependencies during evaluation and updates dependency graph.
    /// </summary>
    private static void RecalculateDerivedProperty(ref PropertyReference derivedProperty, long timestampUtcTicks)
    {
        // TODO(perf): Avoid boxing when possible (use TProperty generic parameter?)

        // Get consolidated data once: provides LastKnownValue + RequiredProperties
        var data = derivedProperty.GetDerivedPropertyData();
        var oldValue = data.LastKnownValue;

        StartRecordingTouchedProperties();
        var newValue = derivedProperty.Metadata.GetValue?.Invoke(derivedProperty.Subject);
        StoreRecordedTouchedProperties(derivedProperty, data);

        data.LastKnownValue = newValue;
        derivedProperty.SetWriteTimestampUtcTicks(timestampUtcTicks);

        // Fire change notification (null source indicates derived property change)
        // Use thread-local storage + static delegates to avoid closure allocation
        _threadLocalOldValue = oldValue;
        using (SubjectChangeContext.WithSource(null))
        {
            derivedProperty.SetPropertyValueWithInterception(newValue, GetOldValueDelegate, NoOpWriteDelegate);
        }

        _threadLocalOldValue = null; // Release reference to prevent stale subject retention

        if (derivedProperty.Subject is IRaisePropertyChanged raiser)
        {
            raiser.RaisePropertyChanged(derivedProperty.Metadata.Name);
        }
    }

    /// <summary>
    /// Flushes deferred Case 2 removals. For targets where TryReplace already replaced
    /// RequiredProperties (recalculated derived properties), Remove() returns false immediately
    /// with no allocation. Only non-recalculated targets pay the CAS cost.
    /// </summary>
    private static void FlushPendingRemovals()
    {
        var pendingRemovals = _pendingRemovals;
        if (pendingRemovals is null || pendingRemovals.Count == 0 || _writePropertyDepth > 0)
        {
            return;
        }

        for (var i = 0; i < pendingRemovals.Count; i++)
        {
            var (target, source) = pendingRemovals[i];
            target.Remove(source);
        }

        pendingRemovals.Clear();
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
    /// Updates forward (required) and backward (usedBy) dependencies using optimistic concurrency control.
    /// Accepts pre-fetched DerivedPropertyData to avoid redundant dictionary lookup.
    /// Strategy:
    /// - Sequential writes: Replace dependencies (exact tracking, removes stale)
    /// - Concurrent writes: Merge dependencies (conservative, keeps all discovered)
    /// - No change: Early exit (allocation-free)
    /// Use version to prevent ABA problem where two threads think they have exclusive access.
    /// </summary>
    private static void StoreRecordedTouchedProperties(PropertyReference derivedProperty, DerivedPropertyData data)
    {
        var recordedDependencies = _recorder!.FinishRecording();
        var requiredProps = data.GetOrCreateRequiredProperties();

        // Read version twice to detect concurrent modifications (allocation-free double-check)
        var version1 = requiredProps.Version;
        var previousItems = requiredProps.Items;
        var version2 = requiredProps.Version;

        // Detect concurrent modification during read
        if (version1 != version2)
        {
            MergeRecordedDependencies(requiredProps, recordedDependencies, derivedProperty);
            _recorder.ClearLastRecording();
            return;
        }

        if (previousItems.SequenceEqual(recordedDependencies))
        {
            _recorder.ClearLastRecording();
            return;
        }

        if (!requiredProps.TryReplace(recordedDependencies, version1))
        {
            // Version changed = concurrent write detected, use conservative merge
            MergeRecordedDependencies(requiredProps, recordedDependencies, derivedProperty);
            _recorder.ClearLastRecording();
            return;
        }

        // Success: Exclusive access confirmed, safe to update backlinks
        // Note: previousItems span still valid (points to old array kept alive by GC)
        // Remove this derived property from old dependencies no longer used
        foreach (ref readonly var oldDependency in previousItems)
        {
            if (!recordedDependencies.Contains(oldDependency))
            {
                oldDependency.GetUsedByProperties().Remove(derivedProperty);
            }
        }

        // Add this derived property to new dependencies
        foreach (ref readonly var newDependency in recordedDependencies)
        {
            if (!previousItems.Contains(newDependency))
            {
                newDependency.GetUsedByProperties().Add(derivedProperty);
            }
        }

        _recorder.ClearLastRecording();
    }

    /// <summary>
    /// Merge mode for concurrent write conflicts - conservatively adds all recorded dependencies.
    /// Never removes dependencies to avoid race conditions. Safe but may accumulate stale dependencies
    /// until next exclusive-access recalculation.
    /// </summary>
    private static void MergeRecordedDependencies(
        DerivedPropertyDependencies requiredProps,
        ReadOnlySpan<PropertyReference> recordedDependencies,
        PropertyReference derivedProperty)
    {
        // Add all recorded dependencies (CAS-based Add() is idempotent and thread-safe)
        foreach (ref readonly var dependency in recordedDependencies)
        {
            requiredProps.Add(dependency);
            dependency.GetUsedByProperties().Add(derivedProperty);
        }
    }
}
