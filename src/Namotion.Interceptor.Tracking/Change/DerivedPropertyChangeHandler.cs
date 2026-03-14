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
/// Nested lock acquisition follows the property dependency graph direction
/// (parent derived → child derived via cascade in <see cref="RecalculateDerivedProperty"/>).
/// A deadlock would require a cycle in the lock acquisition order, which would imply a circular
/// dependency in the property graph — but circular getter dependencies cause infinite recursion
/// before any lock is reached, so the graph is always a DAG and deadlock is impossible.
/// <see cref="DetachProperty"/> acquires at most one lock at a time (Case 1 and Case 2 are
/// sequential, not nested), so it cannot participate in a lock cycle either.
/// </remarks>
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
                // Explicit set handles re-attachment after a prior DetachProperty cleared this flag.
                // On first attach the field default (true) is redundant but harmless.
                data.IsAttached = true;

                // Loop until dependency set stabilizes (same pattern as RecalculateDerivedProperty).
                // Terminates because the set of possible dependencies is finite and each
                // iteration only re-evaluates when the recorded set actually changed.
                object? result;
                try
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

        var data = property.TryGetDerivedPropertyData();
        if (data is null)
        {
            return;
        }

        // Case 1: Derived property detached — remove from dependencies' UsedByProperties.
        // Lock serializes with RecalculateDerivedProperty to prevent zombie backlink resurrection.
        // Gate on IsDerived (immutable) rather than RequiredProperties (mutable): a concurrent
        // RecalculateDerivedProperty or Case 2 could change RequiredProperties between an
        // outside-lock null check and lock acquisition, causing leaked backlinks or missed
        // IsAttached clearing.
        if (property.Metadata.IsDerived)
        {
            lock (data)
            {
                data.IsAttached = false;

                var requiredProperties = data.RequiredProperties;
                if (requiredProperties is not null)
                {
                    foreach (var dependency in requiredProperties.AsSpan())
                    {
                        dependency.TryGetDerivedPropertyData()?.UsedByProperties?.Remove(property);
                    }
                }
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
                        : DerivedPropertyDependencies.RemoveAt(required, index);
                }
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

                // Loop until dependency set stabilizes.
                // Re-evaluation catches concurrent writes to newly-added dependencies
                // that happened before backlinks were registered.
                // Terminates because the set of possible dependencies is finite and each
                // iteration only re-evaluates when the recorded set actually changed.
                object? newValue;
                bool dependenciesChanged;
                do
                {
                    StartRecordingTouchedProperties();
                    newValue = derivedProperty.Metadata.GetValue?.Invoke(derivedProperty.Subject);
                    dependenciesChanged = StoreRecordedTouchedProperties(derivedProperty, data);
                }
                while (dependenciesChanged);

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
        if (_recorder is { IsRecording: true })
        {
            _recorder.FinishRecording();
            _recorder.ClearLastRecording();
        }
    }

    /// <summary>
    /// Updates forward (required) and backward (usedBy) dependencies.
    /// Called under lock(data), so RequiredProperties access is serialized.
    /// Only UsedByProperties updates use CAS (modified by multiple derived properties concurrently).
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
        foreach (ref readonly var newDependency in newDeps)
        {
            if (!previousItems.Contains(newDependency))
            {
                newDependency.GetDerivedPropertyData().GetOrCreateUsedByProperties().Add(derivedProperty);
            }
        }

        return true;
    }
}
