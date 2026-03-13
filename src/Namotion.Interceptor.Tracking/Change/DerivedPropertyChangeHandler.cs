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

    private static readonly Action<IInterceptorSubject, object?> NoOpWriteDelegate = static (_, _) => { };

    /// <inheritdoc />
    public void AttachProperty(SubjectPropertyLifecycleChange change)
    {
        var metadata = change.Property.Metadata;
        if (metadata.IsDerived)
        {
            var data = change.Property.GetDerivedPropertyData();
            lock (data)
            {
                StartRecordingTouchedProperties();

                var result = metadata.GetValue?.Invoke(change.Subject);
                data.LastKnownValue = result;
                change.Property.SetWriteTimestampUtcTicks(SubjectChangeContext.Current.ChangedTimestampUtcTicks);

                StoreRecordedTouchedProperties(change.Property, data);
            }
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
            foreach (var dependency in requiredProperties.AsSpan())
            {
                dependency.TryGetDerivedPropertyData()?.UsedByProperties?.Remove(property);
            }
        }

        // Case 2: Source property detached — remove from dependents' RequiredProperties.
        // Lock the dependent's data and replace the array with the element removed.
        // This serializes with StoreRecordedTouchedProperties (same lock) and allows
        // the detached source subject to be GC'd immediately.
        var usedByProperties = data.UsedByProperties;
        if (usedByProperties is not null && usedByProperties.Count > 0)
        {
            foreach (ref readonly var derivedProperty in usedByProperties.Items)
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
                        : RemoveFromArray(required, index);
                }
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

            data.IsRecalculating = true;
            try
            {
                var oldValue = data.LastKnownValue;

                StartRecordingTouchedProperties();
                var newValue = derivedProperty.Metadata.GetValue?.Invoke(derivedProperty.Subject);
                StoreRecordedTouchedProperties(derivedProperty, data);

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
    /// Updates forward (required) and backward (usedBy) dependencies.
    /// Called under lock(data), so RequiredProperties access is serialized.
    /// Only UsedByProperties updates use CAS (modified by multiple derived properties concurrently).
    /// </summary>
    private static void StoreRecordedTouchedProperties(PropertyReference derivedProperty, DerivedPropertyData data)
    {
        var recordedDependencies = _recorder!.FinishRecording();
        var previousItems = data.RequiredProperties.AsSpan();

        // Steady-state fast path: no allocation if dependencies unchanged.
        if (previousItems.SequenceEqual(recordedDependencies))
        {
            _recorder.ClearLastRecording();
            return;
        }

        // Replace forward dependencies (simple assignment under lock).
        var newItems = recordedDependencies.ToArray();
        data.RequiredProperties = newItems;

        // Update backward links differentially.
        // Remove this derived property from old dependencies no longer used.
        foreach (ref readonly var oldDependency in previousItems)
        {
            if (!recordedDependencies.Contains(oldDependency))
            {
                oldDependency.TryGetDerivedPropertyData()?.UsedByProperties?.Remove(derivedProperty);
            }
        }

        // Add this derived property to new dependencies not previously tracked.
        foreach (ref readonly var newDependency in recordedDependencies)
        {
            if (!previousItems.Contains(newDependency))
            {
                newDependency.GetDerivedPropertyData().GetOrCreateUsedByProperties().Add(derivedProperty);
            }
        }

        _recorder.ClearLastRecording();
    }

    private static PropertyReference[] RemoveFromArray(PropertyReference[] source, int index)
    {
        var result = new PropertyReference[source.Length - 1];
        if (index > 0)
            Array.Copy(source, 0, result, 0, index);
        if (index < source.Length - 1)
            Array.Copy(source, index + 1, result, index, source.Length - index - 1);
        return result;
    }
}
