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
[RunsBefore(typeof(LifecycleInterceptor))]
public class DerivedPropertyChangeHandler : IReadInterceptor, IWriteInterceptor, IPropertyLifecycleHandler
{
    // Lock-free dependency tracking with an allocation-free steady state.
    // Records property reads → builds dependency graph → recalculates dependents on write.

    [ThreadStatic]
    private static DerivedPropertyRecorder? _recorder;

    // Thread-local old value + static delegates avoid closure allocations in recalculation.
    [ThreadStatic]
    private static object? _threadLocalOldValue;

    // Tracks WriteProperty nesting depth. During write-triggered detach, Case 2 removals
    // are deferred to _pendingRemovals and flushed after recalculation (where TryReplace
    // makes most Remove() calls no-ops).
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

        var data = property.TryGetDerivedPropertyData();
        if (data is null)
        {
            return;
        }

        // Case 1: Derived property detached — remove from dependencies' UsedByProperties.
        if (property.Metadata.IsDerived)
        {
            var requiredProperties = data.RequiredProperties;
            if (requiredProperties is not null)
            {
                foreach (ref readonly var dependency in requiredProperties.Items)
                {
                    dependency.TryGetDerivedPropertyData()?.UsedByProperties?.Remove(property);
                }
            }
        }

        // Case 2: Source property detached — remove from dependents' RequiredProperties.
        var usedByProperties = data.UsedByProperties;
        if (usedByProperties is null || usedByProperties.Count == 0)
        {
            return;
        }

        if (_writePropertyDepth > 0)
        {
            // Defer removals — flushed after recalculation (most become no-ops via TryReplace).
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
            // Standalone detach: immediate cleanup.
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
        // The setter modifies internal state, but the actual value is computed by the getter.
        // We need to: 1) re-record dependencies, 2) fire change notification with correct value.
        var metadata = context.Property.Metadata;
        if (metadata is { IsDerived: true, SetValue: not null })
        {
            var currentTimestampUtcTicks = SubjectChangeContext.Current.ChangedTimestampUtcTicks;
            var property = context.Property;
            RecalculateDerivedProperty(ref property, currentTimestampUtcTicks);
        }

        // Use TryGet to avoid allocating DerivedPropertyData for properties with no dependents.
        var usedByProperties = context.Property.TryGetDerivedPropertyData()?.UsedByProperties;
        if (usedByProperties is not null && usedByProperties.Count > 0)
        {
            // Skip dependent recalculation during transaction capture.
            // Derived-with-setter properties bypass the transaction interceptor (IsDerived check),
            // so this guard suppresses cascading recalculations until commit replay.
            if (!SubjectTransaction.HasActiveTransaction ||
                SubjectTransaction.Current is not { IsCommitting: false })
            {
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

        FlushPendingRemovals();
    }

    /// <summary>
    /// Recalculates a derived property when one of its dependencies changes.
    /// </summary>
    private static void RecalculateDerivedProperty(ref PropertyReference derivedProperty, long timestampUtcTicks)
    {
        // TODO(perf): Avoid boxing when possible (use TProperty generic parameter?)
        var data = derivedProperty.GetDerivedPropertyData();
        var oldValue = data.LastKnownValue;

        StartRecordingTouchedProperties();
        var newValue = derivedProperty.Metadata.GetValue?.Invoke(derivedProperty.Subject);
        StoreRecordedTouchedProperties(derivedProperty, data);

        data.LastKnownValue = newValue;
        derivedProperty.SetWriteTimestampUtcTicks(timestampUtcTicks);

        // Fire change notification via thread-local + static delegates (avoids closure allocation).
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
    /// Flushes deferred Case 2 removals (most are no-ops after TryReplace).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
        var requiredProperties = data.GetOrCreateRequiredProperties();

        // Double-read version to detect concurrent modifications.
        var version1 = requiredProperties.Version;
        var previousItems = requiredProperties.Items;
        var version2 = requiredProperties.Version;

        if (version1 != version2)
        {
            MergeRecordedDependencies(requiredProperties, recordedDependencies, derivedProperty);
            _recorder.ClearLastRecording();
            return;
        }

        if (previousItems.SequenceEqual(recordedDependencies))
        {
            _recorder.ClearLastRecording();
            return;
        }

        if (!requiredProperties.TryReplace(recordedDependencies, version1))
        {
            // Concurrent write detected, use conservative merge.
            MergeRecordedDependencies(requiredProperties, recordedDependencies, derivedProperty);
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
    /// Concurrent merge: adds all dependencies without removing (stale entries cleared on next exclusive recalc).
    /// </summary>
    private static void MergeRecordedDependencies(
        DerivedPropertyDependencies requiredProperties,
        ReadOnlySpan<PropertyReference> recordedDependencies,
        PropertyReference derivedProperty)
    {
        foreach (ref readonly var dependency in recordedDependencies)
        {
            requiredProperties.Add(dependency);
            dependency.GetUsedByProperties().Add(derivedProperty);
        }
    }
}
