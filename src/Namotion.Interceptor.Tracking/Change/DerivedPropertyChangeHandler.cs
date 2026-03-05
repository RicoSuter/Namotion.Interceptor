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
        // RequiredProperties is only set for derived properties (during StoreRecordedTouchedProperties),
        // so a non-null check replaces the more expensive property.Metadata.IsDerived lookup.
        var requiredProperties = data.RequiredProperties;
        if (requiredProperties is not null)
        {
            foreach (ref readonly var dependency in requiredProperties.Items)
            {
                dependency.TryGetDerivedPropertyData()?.UsedByProperties?.Remove(property);
            }
        }

        // Case 2: Source property detached — remove from dependents' RequiredProperties.
        // During write-triggered detach, StoreRecordedTouchedProperties will TryReplace
        // the RequiredProperties array anyway, making most of these Remove() calls redundant.
        // Stale backlinks on detached subjects are harmless (subjects become unreachable → GC'd).
        var usedByProperties = data.UsedByProperties;
        if (usedByProperties is not null && usedByProperties.Count > 0)
        {
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
        next(ref context);

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

        var usedByProperties = data.UsedByProperties;
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
