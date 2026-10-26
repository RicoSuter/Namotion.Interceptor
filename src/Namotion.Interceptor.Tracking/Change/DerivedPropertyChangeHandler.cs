using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Handles derived property tracking and automatic recalculation using dependency recording.
/// Runs ahead of LifecycleInterceptor in the chain, pinned by its [RunsBefore] attributes rather
/// than by registration order.
/// </summary>
/// <remarks>
/// Deadlock safety: locks are acquired on per-property <see cref="DerivedPropertyData"/> objects.
/// Nesting follows derived → dependency direction (DAG), so cycles are impossible.
/// <see cref="DetachProperty"/> never holds two locks simultaneously.
/// See docs/design/tracking-derived-properties.md for full concurrency analysis.
/// </remarks>
[RunsBefore(typeof(LifecycleInterceptor))]
// Outer of the change interceptor so the cascade recalculation runs after that interceptor has
// dispatched: a triggering write is announced before the derived recalculations it causes.
[RunsBefore(typeof(PropertyChangeInterceptor))]
public class DerivedPropertyChangeHandler : IReadInterceptor, IWriteInterceptor, IPropertyLifecycleHandler,
    ISingletonContextService<DerivedPropertyChangeHandler>
{
    private static readonly Action<IInterceptorSubject, object?> NoOpWriteDelegate = static (_, _) => { };

    // Safety limit for stabilization loops. Prevents infinite loops from getters
    // with side effects that mutate the tracked state (a user error but shouldn't hang).
    // In correct code, the loop runs 1-2 iterations max.
    private const int MaxStabilizationIterations = 100;

    [ThreadStatic]
    private static DerivedPropertyRecorder? _recorder;

    internal static bool IsRecordingDerivedProperty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _recorder?.IsRecording == true;
    }

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
                Volatile.Write(ref data.IsDerived, true);
                try
                {
                    var value = EvaluateAndStabilize(data, change.Property, callerHoldsLock: true);
                    data.LastKnownValue = value;
                    change.Property.SetWriteTimestamp(SubjectChangeContext.Current.ResolveChangedTimestamp());
                }
                catch (Exception exception) when (exception is not LifecycleContractViolationException)
                {
                    // Getter threw. The value will be computed on the next dependency write. A
                    // contract violation is excluded: absorbing it would hide an illegal getter
                    // behind a derived value that silently never initializes.
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
    public TProperty ReadProperty<TProperty>(ref PropertyReadContext<TProperty> context, ReadInterceptionDelegate<TProperty> next)
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
    /// <remarks>
    /// Committed writes recalculate their dependents even when downstream processing throws.
    /// This requires a truthful <see cref="PropertyWriteContext{TProperty}.IsWritten"/> marker.
    /// Remaining dependents are attempted after a recalculation failure; failures are reported
    /// after completion as one flat list in the order they were raised, with any downstream write
    /// failure first and a nested aggregate replaced by its parts where it stood.
    /// </remarks>
    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        try
        {
            next(ref context);
        }
        catch (Exception writeFailure) when (context.IsWritten)
        {
            try
            {
                CompleteWrite(ref context);
            }
            catch (Exception completionFailure)
            {
                // Flattened by hand rather than through AggregateException.Flatten, which is
                // breadth first and would move the parts of a nested write failure behind the
                // completion failure instead of keeping the write side in front.
                var reported = new List<Exception>();
                AddFlattened(reported, writeFailure);
                AddFlattened(reported, completionFailure);
                throw new AggregateException(reported);
            }

            throw;
        }

        CompleteWrite(ref context);
    }

    // Small enough to inline into both of its call sites, which is what keeps a write on a property
    // no derived value depends on inside the caller's frame. The recalculation body below is far
    // too large for that and stays a call.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CompleteWrite<TProperty>(ref PropertyWriteContext<TProperty> context)
    {
        if (!context.IsWritten)
        {
            return;
        }

        // Signal landed writes before TryGet so writes to not-yet-tracked properties are also detected.
        Interlocked.Increment(ref _writeGeneration);

        var data = context.Property.TryGetDerivedPropertyData();
        if (data is not null)
        {
            RecalculateAfterWrite(ref context, data);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RecalculateAfterWrite<TProperty>(ref PropertyWriteContext<TProperty> context, DerivedPropertyData data)
    {
        List<Exception>? failures = null;

        // Derived-with-setter: value comes from the getter, so setter changes require recalc
        // even when the getter recorded zero deps (e.g. short-circuited at attach).
        if (Volatile.Read(ref data.IsDerived) && context.Property.Metadata.SetValue is not null)
        {
            var rawTimestamp = context.WriteTimestampRaw;
            var storageTimestamp = rawTimestamp > 0 ? rawTimestamp : 0L;
            var property = context.Property;
            try
            {
                RecalculateDerivedProperty(ref property, storageTimestamp, rawTimestamp);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        var usedByProperties = data.GetUsedByProperties();
        if (usedByProperties.Length > 0)
        {
            // Thread the trigger's resolved timestamp into each dependent's context, skipping a
            // scope push. storageTimestamp=0 under a null scope preserves the never-written sentinel.
            var rawTimestamp = context.WriteTimestampRaw;
            var storageTimestamp = rawTimestamp > 0 ? rawTimestamp : 0L;
            RecalculateDependents(usedByProperties, context.Property, storageTimestamp, rawTimestamp, ref failures);
        }

        if (failures is not null)
        {
            // A lone failure keeps its own type so callers can still catch it directly; several are
            // reported flat, because a recalculation can itself surface an aggregate.
            if (failures.Count == 1)
            {
                ExceptionDispatchInfo.Capture(failures[0]).Throw();
            }

            var reported = new List<Exception>(failures.Count);
            foreach (var failure in failures) AddFlattened(reported, failure);
            throw new AggregateException(reported);
        }
    }

    /// <summary>
    /// Appends a failure to the reported list, replacing an aggregate with its parts in place so
    /// the flat list keeps the order the failures were raised in. An empty aggregate carries no
    /// part to report, so it is kept as itself rather than disappearing.
    /// </summary>
    private static void AddFlattened(List<Exception> reported, Exception failure)
    {
        if (failure is AggregateException aggregate && aggregate.InnerExceptions.Count > 0)
        {
            foreach (var inner in aggregate.InnerExceptions) AddFlattened(reported, inner);
            return;
        }

        reported.Add(failure);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RecalculateDependents(ReadOnlySpan<PropertyReference> usedByProperties, PropertyReference triggerProperty, long storageTimestamp, long rawTimestamp, ref List<Exception>? failures)
    {
        for (var i = 0; i < usedByProperties.Length; i++)
        {
            var dependent = usedByProperties[i];
            if (dependent == triggerProperty)
            {
                // Defensive: DerivedPropertyRecorder filters self-refs, so this is unreachable
                // via the normal recorder path.
                continue;
            }

            try
            {
                RecalculateDerivedProperty(ref dependent, storageTimestamp, rawTimestamp);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
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
    internal static void RecalculateDerivedProperty(ref PropertyReference derivedProperty, long storageTimestamp, long rawTimestamp, bool onlyWhenChanged = false)
    {
        // TODO(perf): Avoid boxing when possible (use TProperty generic parameter?)

        // Phase 1: Acquire recalculation ownership (brief lock).
        var data = derivedProperty.GetDerivedPropertyData();
        if (!TryBeginRecalculation(data, out var oldValue))
        {
            return;
        }

        // IsRecalculating stays true during NotifyDerivedPropertyChanged. This serializes
        // notification delivery with recalculation, preventing a stale notification from being
        // delivered after a newer one (TOCTOU race between guard checks and delivery). Ending the
        // recalculation is not a finally: the re-trigger can throw, and on .NET 10.0.0 and 10.0.1 an
        // exception leaving a finally on the normal path skips the catch blocks of the callers.
        try
        {
            RunRecalculation(ref derivedProperty, data, oldValue, storageTimestamp, rawTimestamp, onlyWhenChanged);
        }
        catch (Exception recalculationFailure)
        {
            if (EndRecalculation(data))
            {
                RetriggerAfterFailure(ref derivedProperty, storageTimestamp, rawTimestamp, recalculationFailure);
            }

            throw;
        }

        if (EndRecalculation(data))
        {
            RecalculateDerivedProperty(ref derivedProperty, storageTimestamp, rawTimestamp);
        }
    }

    private static bool TryBeginRecalculation(DerivedPropertyData data, out object? oldValue)
    {
        lock (data)
        {
            oldValue = null;
            if (data.IsRecalculating)
            {
                data.RecalculationNeeded = true;
                return false;
            }

            if (!data.IsAttached)
            {
                return false;
            }

            data.IsRecalculating = true;
            oldValue = data.LastKnownValue;
            return true;
        }
    }

    /// <summary>
    /// Atomically clears IsRecalculating and reports whether a write set RecalculationNeeded in the
    /// gap after the last check, so the derived property must be recalculated again to reflect the
    /// latest state.
    /// </summary>
    /// <remarks>
    /// RecalculationNeeded is cleared before the re-trigger to prevent unbounded recursion when the
    /// getter consistently throws: without clearing, the flag persists (the commit never runs to
    /// clear it), and each re-trigger would re-trigger again indefinitely.
    /// </remarks>
    private static bool EndRecalculation(DerivedPropertyData data)
    {
        lock (data)
        {
            var needsRetrigger = data is { RecalculationNeeded: true, IsAttached: true };
            if (needsRetrigger)
            {
                data.RecalculationNeeded = false;
            }

            data.IsRecalculating = false;
            return needsRetrigger;
        }
    }

    private static void RetriggerAfterFailure(ref PropertyReference derivedProperty, long storageTimestamp, long rawTimestamp, Exception recalculationFailure)
    {
        try
        {
            RecalculateDerivedProperty(ref derivedProperty, storageTimestamp, rawTimestamp);
        }
        catch (Exception retriggerFailure)
        {
            throw new AggregateException(recalculationFailure, retriggerFailure);
        }
    }

    /// <summary>
    /// Evaluates, commits and notifies until no write arrived during delivery. Uses a loop rather than
    /// recursion to prevent stack overflow under sustained concurrent writes. Requires recalculation
    /// ownership.
    /// </summary>
    private static void RunRecalculation(
        ref PropertyReference derivedProperty,
        DerivedPropertyData data,
        object? oldValue,
        long storageTimestamp,
        long rawTimestamp,
        bool onlyWhenChanged)
    {
        for (var outerIteration = 0; outerIteration < MaxStabilizationIterations; outerIteration++)
        {
            if (!TryEvaluateAndCommit(data, ref derivedProperty, storageTimestamp, oldValue, onlyWhenChanged, out var newValue, out var sequence))
            {
                return;
            }

            // Any concurrent writes during delivery set RecalculationNeeded=true and bail out,
            // so no new recalculation (or notification) can start until delivery completes.
            NotifyDerivedPropertyChanged(ref derivedProperty, data, sequence, newValue, oldValue, rawTimestamp);

            lock (data)
            {
                if (!data.RecalculationNeeded || !data.IsAttached)
                {
                    return;
                }

                data.RecalculationNeeded = false;
                oldValue = data.LastKnownValue;
            }
        }

        Trace.TraceWarning(
            $"DerivedPropertyChangeHandler: MaxStabilizationIterations ({MaxStabilizationIterations}) exhausted for " +
            $"'{derivedProperty.Metadata.Name}' on {derivedProperty.Subject.GetType().Name}. " +
            "This indicates a derived getter with circular side effects.");
    }

    /// <summary>
    /// Evaluates without holding lock(data), retries stale results, and commits the value and
    /// timestamp under lock. Returns false when evaluation throws, the property is detached, or the
    /// caller asked to publish only a changed value and it did not change.
    /// </summary>
    private static bool TryEvaluateAndCommit(
        DerivedPropertyData data,
        ref PropertyReference derivedProperty,
        long storageTimestamp,
        object? oldValue,
        bool onlyWhenChanged,
        out object? newValue,
        out long sequence)
    {
        sequence = 0;
        while (true)
        {
            // Phase 2: Evaluate getter OUTSIDE lock(data). Getter side effects can safely enter
            // the lifecycle's topology gate without lock ordering inversion.
            try
            {
                newValue = EvaluateAndStabilize(data, derivedProperty, callerHoldsLock: false);
            }
            catch (Exception exception) when (exception is not LifecycleContractViolationException)
            {
                // Getter threw. Keep LastKnownValue; a concurrent writer's cascade will retry.
                // A contract violation is excluded: this path runs on every dependency write,
                // so absorbing it here would hide the violation on the hot path.
                newValue = null;
                return false;
            }

            // Phase 3: Commit result under lock.
            lock (data)
            {
                if (!data.IsAttached)
                {
                    return false;
                }

                // State changed during evaluation (write, attach, or detach set this flag).
                // Discard the stale result and re-evaluate with a fresh state.
                if (data.RecalculationNeeded)
                {
                    data.RecalculationNeeded = false;
                    continue;
                }

                // The dependencies were updated by the evaluation, which is all such a caller
                // needs when the value is the one already published. A subject is the same value
                // only when it is the same instance, as everywhere in the graph.
                if (onlyWhenChanged && IsPublishedValue(newValue, oldValue))
                {
                    return false;
                }

                data.LastKnownValue = newValue;
                sequence = ++data.RecalculationSequence;
                derivedProperty.SetWriteTimestamp(storageTimestamp);
                return true;
            }
        }
    }

    private static bool IsPublishedValue(object? newValue, object? oldValue)
    {
        return newValue is IInterceptorSubject ? ReferenceEquals(newValue, oldValue) : Equals(newValue, oldValue);
    }

    /// <summary>
    /// Evaluates a derived property again to refresh its dependencies, and publishes the value only
    /// when it differs from the one last published. No-op for a property that is not an attached
    /// derived property.
    /// </summary>
    internal static void RecalculateDerivedPropertyIfChanged(PropertyReference property)
    {
        var data = property.TryGetDerivedPropertyData();
        if (data is null || !Volatile.Read(ref data.IsDerived))
        {
            return;
        }

        var timestamp = SubjectChangeContext.Current.ResolveChangedTimestamp();
        RecalculateDerivedProperty(ref property, timestamp, timestamp, onlyWhenChanged: true);
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
        object? oldValue,
        long rawTimestamp)
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

        // Cascade re-entry: pre-populates the new context's _writeTimestamp with the trigger's
        // raw cached value so the dependent's write skips lazy-resolve (and we therefore do
        // not need a WithChangedTimestamp scope active to share the time with the dependent).
        // newValue is the value the stabilization loop settled on and the value paired with
        // oldValue by the guards above. The cascade re-entry path publishes it rather than
        // re-invoking the getter, which could return a later value that never coexisted with
        // oldValue (see PropertyWriteContext.FinalValueIsNewValue).
        derivedProperty.SetPropertyValueWithInterception(newValue, oldValue, NoOpWriteDelegate, rawTimestamp);

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
        var ownsActiveRecording = false;

        try
        {
            StartRecordingTouchedProperties(property);
            ownsActiveRecording = true;
            var result = property.Metadata.GetValue?.Invoke(property.Subject);
            var recordedDependencies = _recorder!.FinishRecording();
            ownsActiveRecording = false;

            var dependencyUpdate = ReconcileDependencies(data, property, recordedDependencies, callerHoldsLock);
            if (dependencyUpdate != DependencyUpdate.Changed || Volatile.Read(ref _writeGeneration) == generationBefore)
            {
                return result;
            }

            // Concurrent write detected while dependencies changed. Stabilize.
            for (var iteration = 0; iteration < MaxStabilizationIterations; iteration++)
            {
                StartRecordingTouchedProperties(property);
                ownsActiveRecording = true;
                result = property.Metadata.GetValue?.Invoke(property.Subject);
                recordedDependencies = _recorder.FinishRecording();
                ownsActiveRecording = false;

                dependencyUpdate = ReconcileDependencies(data, property, recordedDependencies, callerHoldsLock);
                if (dependencyUpdate == DependencyUpdate.Abandoned)
                {
                    return result;
                }

                if (dependencyUpdate == DependencyUpdate.Unchanged)
                {
                    return result;
                }
            }

            Trace.TraceWarning(
                $"DerivedPropertyChangeHandler: MaxStabilizationIterations ({MaxStabilizationIterations}) exhausted " +
                $"during dependency stabilization for '{property.Metadata.Name}' on {property.Subject.GetType().Name}.");

            return result;
        }
        finally
        {
            DiscardActiveRecording(ownsActiveRecording);
        }
    }

    private enum DependencyUpdate
    {
        Changed,
        Unchanged,
        Abandoned
    }

    /// <summary>
    /// Reconciles dependencies under the caller's lock or a brief lock acquired here, then clears the recording.
    /// When acquiring the lock, abandons the recording if attachment or recalculation state changed.
    /// </summary>
    private static DependencyUpdate ReconcileDependencies(
        DerivedPropertyData data,
        in PropertyReference property,
        ReadOnlySpan<PropertyReference> recordedDependencies,
        bool callerHoldsLock)
    {
        if (callerHoldsLock)
        {
            return data.UpdateDependencies(property, recordedDependencies, _recorder!)
                ? DependencyUpdate.Changed
                : DependencyUpdate.Unchanged;
        }

        lock (data)
        {
            if (!data.IsAttached || data.RecalculationNeeded)
            {
                _recorder!.ClearLastRecording();
                return DependencyUpdate.Abandoned;
            }

            return data.UpdateDependencies(property, recordedDependencies, _recorder!)
                ? DependencyUpdate.Changed
                : DependencyUpdate.Unchanged;
        }
    }

    private static void StartRecordingTouchedProperties(in PropertyReference property)
    {
        _recorder ??= new DerivedPropertyRecorder();
        _recorder.StartRecording(property);
    }

    /// <summary>
    /// Cleans up the recorder state if the getter threw before UpdateDependencies ran.
    /// No-op on the happy path (recording already finished and cleared).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DiscardActiveRecording(bool ownsActiveRecording)
    {
        if (_recorder is null)
        {
            return;
        }

        if (ownsActiveRecording)
        {
            _recorder.FinishRecording();
        }

        // Always clear to prevent thread-static from holding refs to detached subjects.
        _recorder.ClearLastRecording();
    }
}
