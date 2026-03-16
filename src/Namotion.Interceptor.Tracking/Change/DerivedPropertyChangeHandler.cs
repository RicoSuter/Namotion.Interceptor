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
    private static readonly Action<IInterceptorSubject, object?> NoOpWriteDelegate = static (_, _) => { };

    // Safety limit for stabilization loops. Prevents infinite loops from getters
    // with side effects that mutate tracked state (a user error, but shouldn't hang).
    // In correct code, the loop runs 1-2 iterations max.
    private const int MaxStabilizationIterations = 100;

    [ThreadStatic]
    private static DerivedPropertyRecorder? _recorder;

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
                try
                {
                    data.LastKnownValue = EvaluateAndStabilize(data, change.Property);
                    change.Property.SetWriteTimestampUtcTicks(SubjectChangeContext.Current.ChangedTimestampUtcTicks);
                }
                catch (Exception)
                {
                    // Getter threw — value will be computed on the next dependency write.
                }
            }
        }
    }

    /// <inheritdoc />
    public void DetachProperty(SubjectPropertyLifecycleChange change)
    {
        var property = change.Property;

        // Skip properties without tracking data (never participated in the dependency graph).
        var data = property.TryGetDerivedPropertyData();
        if (data is null)
        {
            return;
        }

        // Single lock: set IsAttached=false, clean forward links (Case 1), snapshot backward links (Case 2).
        // Lock serializes with UpdateDependencies' backlink Add on the same depData.
        PropertyReference[] usedBySnapshot;
        lock (data)
        {
            usedBySnapshot = data.DetachAndSnapshotUsedBy(property);
        }

        // Case 2: Remove this property from each dependent's RequiredProperties (outside lock).
        foreach (ref readonly var derivedProperty in usedBySnapshot.AsSpan())
        {
            var derivedData = derivedProperty.TryGetDerivedPropertyData();
            if (derivedData is null)
            {
                continue;
            }

            lock (derivedData)
            {
                derivedData.RemoveRequiredProperty(property);
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

        // Signal write before TryGet so writes to not-yet-tracked properties are also detected.
        Interlocked.Increment(ref _writeGeneration);

        var data = context.Property.TryGetDerivedPropertyData();
        if (data is null)
        {
            return;
        }

        // Derived-with-setter: Setter modifies state, but value comes from the getter → recalculate.
        if (data.HasRequiredProperties && context.Property.Metadata.SetValue is not null)
        {
            var currentTimestampUtcTicks = SubjectChangeContext.Current.ChangedTimestampUtcTicks;
            var property = context.Property;
            RecalculateDerivedProperty(ref property, currentTimestampUtcTicks);
        }

        var usedByProperties = data.GetUsedByProperties();
        if (usedByProperties.Length > 0)
        {
            // Suppress cascading recalculations during transaction capture (replayed on commit).
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
    }

    /// <summary>
    /// Recalculates a derived property: re-evaluates getter, updates deps, fires change notification.
    /// Locks per-property data to serialize concurrent recalculations.
    /// </summary>
    private static void RecalculateDerivedProperty(ref PropertyReference derivedProperty, long timestampUtcTicks)
    {
        // TODO(perf): Avoid boxing when possible (use TProperty generic parameter?)

        var data = derivedProperty.GetDerivedPropertyData();
        object? oldValue;
        object? newValue;
        long sequence;

        lock (data)
        {
            // Reentrancy guard: derived-with-setter → SetPropertyValueWithInterception → WriteProperty
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
                oldValue = data.LastKnownValue;

                try
                {
                    newValue = EvaluateAndStabilize(data, derivedProperty);
                }
                catch (Exception)
                {
                    // Getter threw — keep LastKnownValue, concurrent writer's cascade will retry.
                    return;
                }

                data.LastKnownValue = newValue;
                sequence = ++data.RecalculationSequence;
                derivedProperty.SetWriteTimestampUtcTicks(timestampUtcTicks);
            }
            finally
            {
                data.IsRecalculating = false;
            }
        }

        // Notifications outside lock(data) to avoid deadlock with lock(_attachedSubjects).
        // Skip stale notifications so the last one always reflects the final state.
        if (sequence != Volatile.Read(ref data.RecalculationSequence))
        {
            return;
        }

        using (SubjectChangeContext.WithSource(null))
        {
            derivedProperty.SetPropertyValueWithInterception(newValue, oldValue, NoOpWriteDelegate);
        }

        if (derivedProperty.Subject is IRaisePropertyChanged raiser)
        {
            raiser.RaisePropertyChanged(derivedProperty.Metadata.Name);
        }
    }

    /// <summary>
    /// Evaluates a derived property getter, records dependencies, and runs the stabilization
    /// loop if concurrent writes changed the dependency set. Called under lock(data).
    /// </summary>
    private static object? EvaluateAndStabilize(DerivedPropertyData data, in PropertyReference property)
    {
        try
        {
            var generationBefore = Volatile.Read(ref _writeGeneration);

            StartRecordingTouchedProperties();
            var result = property.Metadata.GetValue?.Invoke(property.Subject);
            var recordedDeps = _recorder!.FinishRecording();
            var dependenciesChanged = data.UpdateDependencies(property, recordedDeps, _recorder);

            if (dependenciesChanged && Volatile.Read(ref _writeGeneration) != generationBefore)
            {
                var iterations = 0;
                do
                {
                    if (++iterations > MaxStabilizationIterations)
                    {
                        break;
                    }

                    StartRecordingTouchedProperties();
                    result = property.Metadata.GetValue?.Invoke(property.Subject);
                    recordedDeps = _recorder.FinishRecording();
                    dependenciesChanged = data.UpdateDependencies(property, recordedDeps, _recorder);
                }
                while (dependenciesChanged);
            }

            return result;
        }
        finally
        {
            DiscardActiveRecording();
        }
    }

    private static void StartRecordingTouchedProperties()
    {
        _recorder ??= new DerivedPropertyRecorder();
        _recorder.StartRecording();
    }

    /// <summary>
    /// Cleans up the recorder state if the getter threw before UpdateDependencies ran.
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
}
