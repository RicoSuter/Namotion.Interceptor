using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Handles derived property tracking and automatic recalculation using dependency recording.
/// Requires LifecycleInterceptor to be added after this interceptor.
/// </summary>
public class DerivedPropertyChangeHandler : IReadInterceptor, IWriteInterceptor, IPropertyLifecycleHandler
{
    // - Records property reads during derived property evaluation
    // - Builds dependency graph: derived property → source properties
    // - When source property changes, recalculates all dependent derived properties
    // - Uses optimistic concurrency control to handle concurrent updates safely

    // Thread Safety: Lock-free with allocation-free steady state. Handles concurrent writes via merge mode.
    
    [ThreadStatic]
    private static DerivedPropertyRecorder? _recorder;

    /// <inheritdoc />
    public void AttachProperty(SubjectPropertyLifecycleChange change)
    {
        if (change.Property.Metadata.IsDerived)
        {
            StartRecordingTouchedProperties();

            var result = change.Property.Metadata.GetValue?.Invoke(change.Subject);
            change.Property.SetLastKnownValue(result);
            change.Property.SetWriteTimestamp(SubjectChangeContext.Current.ChangedTimestamp);
            
            StoreRecordedTouchedProperties(change.Property);
        }
    }

    /// <inheritdoc />
    public void DetachProperty(SubjectPropertyLifecycleChange change)
    {
        // No cleanup needed - dependencies are managed per-property
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

        // Fast path: Skip if no derived properties depend on this property
        // Check this first as it's more likely to early-exit than transaction check
        var usedByProperties = context.Property.GetUsedByProperties().Items;
        if (usedByProperties.Length == 0)
        {
            return;
        }

        // Skip derived property recalculation during transaction capture
        // (derived values will be recalculated from pending values when read)
        var transaction = SubjectTransaction.Current;
        if (transaction is { IsCommitting: false })
        {
            return;
        }

        var timestamp = SubjectChangeContext.Current.ChangedTimestamp;
        for (var i = 0; i < usedByProperties.Length; i++)
        {
            var dependent = usedByProperties[i];
            if (dependent == context.Property)
            {
                continue; // Skip self-references (rare edge case)
            }

            RecalculateDerivedProperty(ref dependent, ref timestamp);
        }
    }

    /// <summary>
    /// Recalculates a derived property when one of its dependencies changes.
    /// Records new dependencies during evaluation and updates dependency graph.
    /// </summary>
    private static void RecalculateDerivedProperty(ref PropertyReference derivedProperty, ref DateTimeOffset timestamp)
    {
        // TODO(perf): Avoid boxing when possible (use TProperty generic parameter?)

        var oldValue = derivedProperty.GetLastKnownValue();
        StartRecordingTouchedProperties();
        var newValue = derivedProperty.Metadata.GetValue?.Invoke(derivedProperty.Subject);
        StoreRecordedTouchedProperties(derivedProperty);
        
        derivedProperty.SetLastKnownValue(newValue);
        derivedProperty.SetWriteTimestamp(timestamp);

        // Fire change notification (null source indicates derived property change)
        using (SubjectChangeContext.WithSource(null))
        {
            derivedProperty.SetPropertyValueWithInterception(newValue, _ => oldValue, delegate { });
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
