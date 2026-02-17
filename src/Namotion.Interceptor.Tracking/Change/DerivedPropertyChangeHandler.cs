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

    private static readonly Func<IInterceptorSubject, object?> GetOldValueDelegate = static _ => _threadLocalOldValue;
    private static readonly Action<IInterceptorSubject, object?> NoOpWriteDelegate = static (_, _) => { };

    /// <inheritdoc />
    public void AttachProperty(SubjectPropertyLifecycleChange change)
    {
        var metadata = change.Property.Metadata;
        if (metadata.IsDerived)
        {
            StartRecordingTouchedProperties();

            var result = metadata.GetValue?.Invoke(change.Subject);
            change.Property.SetLastKnownValue(result);
            change.Property.SetWriteTimestampUtcTicks(SubjectChangeContext.Current.ChangedTimestampUtcTicks);
            
            StoreRecordedTouchedProperties(change.Property);
        }
    }

    /// <inheritdoc />
    public void DetachProperty(SubjectPropertyLifecycleChange change)
    {
        var property = change.Property;

        // Case 1: This is a derived property being detached.
        // Remove it from all its dependencies' UsedByProperties to break backward refs.
        if (property.Metadata.IsDerived)
        {
            var requiredProps = property.TryGetRequiredProperties();
            if (requiredProps is not null)
            {
                foreach (ref readonly var dependency in requiredProps.Items)
                {
                    // Use TryGet to avoid allocating on dependencies that have no UsedByProperties
                    dependency.TryGetUsedByProperties()?.Remove(property);
                }
            }
        }

        // Case 2: This property (source or derived) might be used by derived properties on OTHER subjects.
        // Remove this property from those derived properties' RequiredProperties to prevent memory leaks.
        //
        // Performance note: We intentionally DON'T clear this property's UsedByProperties for two reasons:
        // 1. Detached subjects have their context/fallback removed, so recalculation won't affect the live graph
        // 2. The stale entries will be GC'd along with the detached subject - no memory leak
        // This avoids the overhead of iterating and removing from potentially many derived properties.
        var usedByProps = property.TryGetUsedByProperties();
        if (usedByProps is null || usedByProps.Count == 0)
        {
            return; // No dependencies to clean up - fast path, no allocation
        }

        foreach (ref readonly var derivedProp in usedByProps.Items)
        {
            derivedProp.TryGetRequiredProperties()?.Remove(property);
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

        // Check this first as it's more likely to early-exit than transaction check
        // Use TryGet to avoid allocating DerivedPropertyDependencies for properties with no dependents
        var usedByProps = context.Property.TryGetUsedByProperties();
        if (usedByProps is null || usedByProps.Count == 0)
        {
            return;
        }

        var usedByProperties = usedByProps.Items;

        // Skip dependent recalculation during transaction capture.
        // The [RunsBefore] ordering (Transaction before Derived) prevents non-derived writes
        // from reaching here during capture. However, derived-with-setter properties bypass
        // the transaction interceptor (IsDerived check), so this guard is still needed to
        // suppress cascading recalculations until commit replay.
        if (SubjectTransaction.HasActiveTransaction &&
            SubjectTransaction.Current is { IsCommitting: false })
        {
            return;
        }

        var timestampUtcTicks = SubjectChangeContext.Current.ChangedTimestampUtcTicks;
        for (var i = 0; i < usedByProperties.Length; i++)
        {
            var dependent = usedByProperties[i];
            if (dependent == context.Property)
            {
                continue; // Skip self-references (rare edge case)
            }

            RecalculateDerivedProperty(ref dependent, timestampUtcTicks);
        }
    }

    /// <summary>
    /// Recalculates a derived property when one of its dependencies changes.
    /// Records new dependencies during evaluation and updates dependency graph.
    /// </summary>
    private static void RecalculateDerivedProperty(ref PropertyReference derivedProperty, long timestampUtcTicks)
    {
        // TODO(perf): Avoid boxing when possible (use TProperty generic parameter?)

        var oldValue = derivedProperty.GetLastKnownValue();
        StartRecordingTouchedProperties();
        var newValue = derivedProperty.Metadata.GetValue?.Invoke(derivedProperty.Subject);
        StoreRecordedTouchedProperties(derivedProperty);

        derivedProperty.SetLastKnownValue(newValue);
        derivedProperty.SetWriteTimestampUtcTicks(timestampUtcTicks);

        // Fire change notification (null source indicates derived property change)
        // Use thread-local storage + static delegates to avoid closure allocation
        _threadLocalOldValue = oldValue;
        using (SubjectChangeContext.WithSource(null))
        {
            derivedProperty.SetPropertyValueWithInterception(newValue, GetOldValueDelegate, NoOpWriteDelegate);
        }

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
    /// Strategy:
    /// - Sequential writes: Replace dependencies (exact tracking, removes stale)
    /// - Concurrent writes: Merge dependencies (conservative, keeps all discovered)
    /// - No change: Early exit (allocation-free)
    /// Use version to prevent ABA problem where two threads think they have exclusive access.
    /// </summary>
    private static void StoreRecordedTouchedProperties(PropertyReference derivedProperty)
    {
        var recordedDependencies = _recorder!.FinishRecording();
        var requiredProps = derivedProperty.GetRequiredProperties();

        // Read version twice to detect concurrent modifications (allocation-free double-check)
        var version1 = requiredProps.Version;
        var previousItems = requiredProps.Items;
        var version2 = requiredProps.Version;

        // Detect concurrent modification during read
        if (version1 != version2)
        {
            MergeRecordedDependencies(requiredProps, recordedDependencies, derivedProperty);
            return;
        }

        if (previousItems.SequenceEqual(recordedDependencies))
        {
            return;
        }

        if (!requiredProps.TryReplace(recordedDependencies, version1))
        {
            // Version changed = concurrent write detected, use conservative merge
            MergeRecordedDependencies(requiredProps, recordedDependencies, derivedProperty);
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
