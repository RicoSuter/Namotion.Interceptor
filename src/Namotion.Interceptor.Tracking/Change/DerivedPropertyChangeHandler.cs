using System.Diagnostics;
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
    // with side effects that mutate the tracked state (a user error but shouldn't hang).
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
            // Signal any in-progress recalculation to re-evaluate after we change state.
            if (data.IsRecalculating)
            {
                data.RecalculationNeeded = true;
            }

            data.IsAttached = true;
            if (metadata.IsDerived)
            {
                try
                {
                    data.LastKnownValue = EvaluateAndStabilize(data, change.Property, callerHoldsLock: true);
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

        // Single lock: set IsAttached=false, clean dependencies (Case 1), snapshot used-by properties (Case 2).
        // Lock serializes with UpdateDependencies' used-by Add on the same depData.
        PropertyReference[] usedBySnapshot;
        lock (data)
        {
            // Signal any in-progress recalculation to re-evaluate after we change state.
            if (data.IsRecalculating)
            {
                data.RecalculationNeeded = true;
            }

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
    /// The getter is evaluated OUTSIDE lock(data) to prevent deadlock with lock(_attachedSubjects)
    /// in LifecycleInterceptor when getters have side effects that write to subject-typed properties.
    /// IsRecalculating serializes concurrent recalculations; RecalculationNeeded catches state changes
    /// (writes, attach, detach) that occur during the unlocked evaluation window.
    /// </summary>
    private static void RecalculateDerivedProperty(ref PropertyReference derivedProperty, long timestampUtcTicks)
    {
        // TODO(perf): Avoid boxing when possible (use TProperty generic parameter?)

        object? oldValue;

        // Phase 1: Acquire recalculation ownership (brief lock).
        var data = derivedProperty.GetDerivedPropertyData();
        lock (data)
        {
            if (data.IsRecalculating)
            {
                data.RecalculationNeeded = true;
                return;
            }

            if (!data.IsAttached)
            {
                return;
            }

            data.IsRecalculating = true;
            oldValue = data.LastKnownValue;
        }

        // Outer loop handles the post-notification RecalculationNeeded check without recursion,
        // preventing stack overflow under sustained concurrent writes.
        // The try-finally at this level ensures IsRecalculating is always cleared on exit.
        // Crucially, IsRecalculating stays true during NotifyDerivedPropertyChanged — this
        // serializes notification delivery with recalculation, preventing a stale notification
        // from being delivered after a newer one (TOCTOU race between guard checks and delivery).
        try
        {
            for (var outerIteration = 0; outerIteration < MaxStabilizationIterations; outerIteration++)
            {
                object? newValue;
                long sequence;

                // Inner loop: re-evaluates when the state changes during evaluation.
                while (true)
                {
                    // Phase 2: Evaluate getter OUTSIDE lock(data).
                    // This prevents deadlock: getter side effects can safely acquire
                    // lock(_attachedSubjects) in LifecycleInterceptor without lock ordering inversion.
                    try
                    {
                        newValue = EvaluateAndStabilize(data, derivedProperty, callerHoldsLock: false);
                    }
                    catch (Exception)
                    {
                        // Getter threw — keep LastKnownValue, concurrent writer's cascade will retry.
                        return;
                    }

                    // Phase 3: Commit result under lock.
                    lock (data)
                    {
                        if (!data.IsAttached)
                        {
                            return;
                        }

                        // State changed during evaluation (write, attach, or detach set this flag).
                        // Discard the stale result and re-evaluate with a fresh state.
                        if (data.RecalculationNeeded)
                        {
                            data.RecalculationNeeded = false;
                            continue;
                        }

                        data.LastKnownValue = newValue;
                        sequence = ++data.RecalculationSequence;
                        derivedProperty.SetWriteTimestampUtcTicks(timestampUtcTicks);
                        break;
                    }
                }

                // Deliver notification while IsRecalculating is still true.
                // Any concurrent writes during delivery set RecalculationNeeded=true and bail out,
                // so no new recalculation (or notification) can start until delivery completes.
                NotifyDerivedPropertyChanged(ref derivedProperty, data, sequence, newValue, oldValue);

                // Handle recalculations that arrived during evaluation or notification delivery.
                // Uses a loop (not recursion) to prevent stack overflow under sustained concurrent writes.
                lock (data)
                {
                    if (!data.RecalculationNeeded || !data.IsAttached)
                    {
                        return;
                    }

                    data.RecalculationNeeded = false;
                    // IsRecalculating stays true for next iteration
                    oldValue = data.LastKnownValue;
                }
            }

            // Safety: if the outer loop exhausted MaxStabilizationIterations, log a warning.
            Trace.TraceWarning(
                $"DerivedPropertyChangeHandler: MaxStabilizationIterations ({MaxStabilizationIterations}) exhausted for " +
                $"'{derivedProperty.Metadata.Name}' on {derivedProperty.Subject.GetType().Name}. " +
                "This indicates a derived getter with circular side effects.");
        }
        finally
        {
            // Atomically clear IsRecalculating. If a write set RecalculationNeeded
            // in the gap between the outer loop's return-check and this finally,
            // we must re-trigger so the derived property reflects the latest state.
            bool needsRetrigger;
            lock (data)
            {
                needsRetrigger = data is { RecalculationNeeded: true, IsAttached: true };
                data.IsRecalculating = false;
            }

            if (needsRetrigger)
            {
                RecalculateDerivedProperty(ref derivedProperty, timestampUtcTicks);
            }
        }
    }

    /// <summary>
    /// Fires change notification for a recalculated derived property.
    /// Called outside lock(data) to avoid deadlock with lock(_attachedSubjects) in LifecycleInterceptor.
    /// Skips if a newer recalculation already completed (stale sequence or overwritten value).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void NotifyDerivedPropertyChanged(
        ref PropertyReference derivedProperty,
        DerivedPropertyData data,
        long sequence,
        object? newValue,
        object? oldValue)
    {
        if (sequence != Volatile.Read(ref data.RecalculationSequence))
        {
            return;
        }

        // Safe for boxed value types: each computation produces a distinct reference.
        if (!ReferenceEquals(newValue, Volatile.Read(ref data.LastKnownValue)))
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
    /// loop if concurrent writes changed the dependency set.
    /// When <paramref name="callerHoldsLock"/> is true (AttachProperty), the caller already holds
    /// lock(data) and lock(_attachedSubjects), so UpdateDependencies runs directly.
    /// When false (RecalculateDerivedProperty), the lock is acquired only briefly for
    /// UpdateDependencies, preventing deadlock between lock(data) and lock(_attachedSubjects)
    /// when getters have side effects that write to subject-typed properties.
    /// </summary>
    private static object? EvaluateAndStabilize(
        DerivedPropertyData data, in PropertyReference property, bool callerHoldsLock)
    {
        var generationBefore = Volatile.Read(ref _writeGeneration);

        try
        {
            StartRecordingTouchedProperties();
            var result = property.Metadata.GetValue?.Invoke(property.Subject);
            var recordedDeps = _recorder!.FinishRecording();

            bool dependenciesChanged;
            if (callerHoldsLock)
            {
                dependenciesChanged = data.UpdateDependencies(property, recordedDeps, _recorder);
            }
            else
            {
                lock (data)
                {
                    if (!data.IsAttached || data.RecalculationNeeded)
                    {
                        _recorder.ClearLastRecording();
                        return result;
                    }

                    dependenciesChanged = data.UpdateDependencies(property, recordedDeps, _recorder);
                }
            }

            if (!dependenciesChanged || Volatile.Read(ref _writeGeneration) == generationBefore)
            {
                return result;
            }

            // Concurrent write detected while dependencies changed — stabilize.
            for (var iteration = 0; iteration < MaxStabilizationIterations; iteration++)
            {
                StartRecordingTouchedProperties();
                result = property.Metadata.GetValue?.Invoke(property.Subject);
                recordedDeps = _recorder.FinishRecording();

                if (callerHoldsLock)
                {
                    if (!data.UpdateDependencies(property, recordedDeps, _recorder))
                    {
                        break;
                    }
                }
                else
                {
                    lock (data)
                    {
                        if (!data.IsAttached || data.RecalculationNeeded)
                        {
                            _recorder.ClearLastRecording();
                            return result;
                        }

                        if (!data.UpdateDependencies(property, recordedDeps, _recorder))
                        {
                            break;
                        }
                    }
                }
            }

            Trace.TraceWarning(
                $"DerivedPropertyChangeHandler: MaxStabilizationIterations ({MaxStabilizationIterations}) exhausted " +
                $"during dependency stabilization for '{property.Metadata.Name}' on {property.Subject.GetType().Name}.");

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
