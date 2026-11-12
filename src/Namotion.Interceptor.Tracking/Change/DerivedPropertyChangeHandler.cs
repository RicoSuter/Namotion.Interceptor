using System.Collections;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Handles derived property tracking and automatic recalculation using dependency recording.
/// Requires LifecycleInterceptor to be added after this interceptor.
/// </summary>
/// <remarks>
/// Deadlock safety: locks are acquired on per-property <see cref="DerivedPropertyData"/> objects.
/// Nesting follows derived → dependency direction (DAG), so cycles are impossible.
/// <see cref="DetachProperty"/> never holds lock(data) and lock(derivedData) at the same time;
/// the used-by collection's leaf lock it may take acquires nothing further.
/// See docs/design/tracking-derived-properties.md for full concurrency analysis.
/// </remarks>
[RunsBefore(typeof(LifecycleInterceptor))]
// Outer of the change interceptor so the cascade recalculation runs after that interceptor has
// dispatched: a triggering write is announced before the derived recalculations it causes. Only
// load-bearing under aggregation, where instances would otherwise interleave and one context's
// cascade could be announced before another's dispatch of the triggering write.
[RunsBefore(typeof(PropertyChangeInterceptor))]
public class DerivedPropertyChangeHandler : IReadInterceptor, IWriteInterceptor, IPropertyLifecycleHandler
{
    // Safety limit for stabilization loops. Prevents infinite loops from getters
    // with side effects that mutate the tracked state (a user error but shouldn't hang).
    // In correct code, the loop runs 1-2 iterations max.
    private const int MaxStabilizationIterations = 100;

    [ThreadStatic]
    private static DerivedPropertyRecorder? _recorder;

    // Writes that can attach subjects and have not returned yet on this thread, and the derived properties whose
    // evaluation at the end of an attach found a changed value while such a write was running. The outermost of those
    // writes recalculates them once it has been announced.
    [ThreadStatic]
    private static int _attachingWriteDepth;

    [ThreadStatic]
    private static List<PropertyReference>? _changedAfterAttach;

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
        // Source data is never kept by a detach, so its current data is already attached.
        if (!change.Property.Metadata.IsDerived)
        {
            return;
        }

        while (!TryAttach(change.Property.GetDerivedPropertyData(), change.Property))
        {
            // A detach removed the fetched data before it was locked: attach the current data instead.
        }

        // The getter records only reads of attached subjects, so it misses a dependency on a subject attached later in
        // the same operation, such as a sibling of an ancestor.
        AttachOperation.Current?.RunAtEndIfSubjectsAttachedLater(EvaluateAfterAttach, change.Property);
    }

    /// <summary>
    /// Evaluates an attached derived property again to record dependencies on subjects attached after it, if it has no
    /// dependencies or depends on a property that can hold subjects by now. Returns the recalculation to run once the
    /// thread holds no lifecycle lock if the value changed from the one of an earlier evaluation.
    /// </summary>
    // Evaluated like a recalculation, not like the evaluation at attach: the property may have dependencies by now, so
    // a write on another thread may ask for lock(data) while the getter runs, and the getter may wait for that thread.
    private static Action<PropertyReference>? EvaluateAfterAttach(PropertyReference property)
    {
        var data = property.TryGetDerivedPropertyData();
        if (data is null || !TryBeginEvaluationAfterAttach(data, out var lastKnownValue))
        {
            return null;
        }

        // Without a value from an earlier evaluation, a type without null gets its first value, which is not a change.
        var isFirstValue = lastKnownValue is null && !CanBeNull(property.Metadata.Type);
        object? value = null;
        var isChanged = false;
        var isEvaluated = false;
        try
        {
            value = EvaluateAndStabilize(data, property, callerHoldsLock: false, out var dependenciesChanged);

            // Compared without lock(data) as well: Equals is code of the application, like the getter.
            isChanged = !isFirstValue && IsChangedValue(value, lastKnownValue, dependenciesChanged);
            isEvaluated = true;
        }
        catch (Exception)
        {
            // The getter or the comparison threw. The value will be computed on the next write to a dependency it
            // has recorded.
        }

        lock (data)
        {
            data.IsRecalculating = false;
            if (!data.IsAttached)
            {
                return null;
            }

            if (data.RecalculationNeeded || isChanged)
            {
                // The value changed, or a write or an attach found this evaluation in flight and left its
                // recalculation to it. LastKnownValue stays as it is, so that the recalculation publishes from it.
                data.RecalculationNeeded = false;
                data.PublicationPendingAfterAttachThreadId = Environment.CurrentManagedThreadId;
                return RecalculateAfterAttach;
            }

            if (isEvaluated)
            {
                // Not published, but it is what the getter returns by now, so the next change is one from this value.
                data.LastKnownValue = value;
                if (isFirstValue)
                {
                    property.SetWriteTimestamp(SubjectChangeContext.Current.ResolveChangedTimestamp());
                }
            }

            return null;
        }
    }

    private static bool CanBeNull(Type type)
    {
        return !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;
    }

    /// <summary>
    /// Takes recalculation ownership for an evaluation at the end of an attach. False when there is nothing to
    /// evaluate, or when a recalculation in flight evaluates the property instead.
    /// </summary>
    private static bool TryBeginEvaluationAfterAttach(DerivedPropertyData data, out object? lastKnownValue)
    {
        lastKnownValue = null;
        lock (data)
        {
            if (!data.IsAttached || !Volatile.Read(ref data.IsDerived))
            {
                return false;
            }

            if (data.IsRecalculating)
            {
                // The recalculation in flight evaluates again, records the dependencies and publishes.
                data.RecalculationNeeded = true;
                return false;
            }

            // A publication is pending, of an operation of another context that attached the property as well: its
            // recalculation evaluates again, and LastKnownValue has to stay the value it publishes from.
            if (data.PublicationPendingAfterAttachThreadId != 0)
            {
                return false;
            }

            // Without dependencies the evaluation at attach read no attached subject, or it threw.
            if (!data.RequiredPropertiesSpan.IsEmpty && !DependsOnPropertyThatCanHoldSubjects(data))
            {
                return false;
            }

            data.IsRecalculating = true;
            data.RecalculationNeeded = false;
            lastKnownValue = data.LastKnownValue;
            return true;
        }
    }

    // A different value is a change only if the evaluation also recorded other dependencies than before, the sign that
    // the first one missed a subject. Otherwise it may be one value that the getter builds anew on every call, such as
    // a list or a struct that holds one, and would look changed on every attach. Two one-dimensional arrays are a
    // change only if their items differ too.
    private static bool IsChangedValue(object? value, object? lastKnownValue, bool dependenciesChanged)
    {
        return dependenciesChanged && !Equals(value, lastKnownValue) && !HaveSameItems(value, lastKnownValue);
    }

    // Items are compared with Equals: a structural comparison of the items throws for an array of another rank.
    private static bool HaveSameItems(object? value, object? lastKnownValue)
    {
        return value?.GetType().IsSZArray is true &&
            lastKnownValue?.GetType().IsSZArray is true &&
            ((IStructuralEquatable)value).Equals(lastKnownValue, EqualityComparer<object>.Default);
    }

    // Publishes, so it runs only once the thread holds no lifecycle lock: an observer on another thread may hold the
    // dispatch lock while it waits for a lifecycle lock. Within a write that can attach subjects it waits further, for
    // that write to be announced, so that a subject is never announced after the derived properties in it.
    private static void RecalculateAfterAttach(PropertyReference property)
    {
        if (_attachingWriteDepth > 0)
        {
            (_changedAfterAttach ??= []).Add(property);
            return;
        }

        try
        {
            var timestamp = SubjectChangeContext.Current.ResolveChangedTimestamp();
            RecalculateDerivedProperty(ref property, timestamp, timestamp, onlyIfPendingAfterAttach: true);
        }
        catch (Exception exception)
        {
            // Outside a write there is no writer to report it to: attaching or detaching a context fails only for
            // its own reasons, and an observer that throws must not cut it short or replace its exception.
            Trace.TraceWarning(
                $"DerivedPropertyChangeHandler: publishing '{property.Name}' on {property.Subject.GetType().Name} " +
                $"after an attach failed: {exception}");
        }
    }

    private static bool TryAttach(DerivedPropertyData data, in PropertyReference property)
    {
        lock (data)
        {
            if (!ReferenceEquals(property.TryGetDerivedPropertyData(), data))
            {
                return false;
            }

            // Signal any in-progress recalculation to re-evaluate after we change state.
            if (data.IsRecalculating)
            {
                data.RecalculationNeeded = true;
            }

            data.IsAttached = true;
            data.PublicationPendingAfterAttachThreadId = 0;
            Volatile.Write(ref data.IsDerived, true);
            try
            {
                data.LastKnownValue = EvaluateAndStabilize(data, property, callerHoldsLock: true, out _);
                property.SetWriteTimestamp(SubjectChangeContext.Current.ResolveChangedTimestamp());
            }
            catch (Exception)
            {
                // Getter threw. The value will be computed on the next dependency write.
            }

            return true;
        }
    }

    // A getter reaches another subject through tracked properties only by reading a property that can hold subjects.
    // A subject reached through a field or a service is not considered, see docs/tracking.md, Reaching Other Subjects.
    private static bool DependsOnPropertyThatCanHoldSubjects(DerivedPropertyData data)
    {
        foreach (ref readonly var dependency in data.RequiredPropertiesSpan)
        {
            if (dependency.Metadata.Type.CanContainSubjects())
            {
                return true;
            }
        }

        return false;
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
            usedBySnapshot = data.DetachAndSnapshotUsedBy(property);
            if (data.IsRecalculating)
            {
                // Kept, so that its IsRecalculating serializes the in-flight recalculation with the ones after
                // a re-attach. Signal it to re-evaluate after we change state.
                data.RecalculationNeeded = true;
            }
            else
            {
                // Removed under the lock, so that a registration or attach that fetched this instance sees
                // that it is no longer current and uses the current data instead.
                property.RemoveDerivedPropertyData(data);
            }
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
                // A dependent that registered on this property's current data after the snapshot keeps it.
                if (!IsUsedByCurrentData(property, derivedProperty))
                {
                    derivedData.RemoveRequiredProperty(property);
                }
            }
        }
    }

    private static bool IsUsedByCurrentData(in PropertyReference property, in PropertyReference derivedProperty)
    {
        return property.TryGetDerivedPropertyData()?.UsedByDependencies?.Contains(derivedProperty) is true;
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
    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        // Only a write that the lifecycle interceptor reconciles attaches subjects. For a type without references the
        // JIT resolves this per TProperty, so such a write pays nothing for the bookkeeping below, and neither it nor
        // the write of a string reads the metadata.
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<TProperty>() ||
            typeof(TProperty) == typeof(string) ||
            !LifecycleInterceptor.CanAttachSubjects<TProperty>(context.Property.Metadata))
        {
            WriteAndRecalculateDependents(ref context, next);
            return;
        }

        _attachingWriteDepth++;
        var isWritten = false;
        try
        {
            WriteAndRecalculateDependents(ref context, next);
            isWritten = true;
        }
        finally
        {
            // A write that failed still recalculates, since the subjects it attached before stay attached, but its
            // exception wins.
            if (--_attachingWriteDepth == 0 && _changedAfterAttach is not null)
            {
                RecalculateChangedAfterAttach(context.WriteTimestampRaw, throwOnFailure: isWritten);
            }
        }
    }

    private static void RecalculateChangedAfterAttach(long rawTimestamp, bool throwOnFailure)
    {
        // Taken over first, because an observer may start a write that collects and recalculates its own.
        var properties = _changedAfterAttach!;
        _changedAfterAttach = null;

        var storageTimestamp = rawTimestamp > 0 ? rawTimestamp : 0L;
        ExceptionDispatchInfo? firstException = null;
        foreach (var changedProperty in properties)
        {
            var property = changedProperty;
            try
            {
                RecalculateDerivedProperty(ref property, storageTimestamp, rawTimestamp, onlyIfPendingAfterAttach: true);
            }
            catch (Exception exception)
            {
                // An observer threw: the other properties are still recalculated.
                firstException ??= ExceptionDispatchInfo.Capture(exception);
            }
        }

        if (throwOnFailure)
        {
            firstException?.Throw();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteAndRecalculateDependents<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        next(ref context);

        if (!context.IsWritten)
        {
            return;
        }

        // Signal landed writes before TryGet so writes to not-yet-tracked properties are also detected.
        Interlocked.Increment(ref _writeGeneration);

        var data = context.Property.TryGetDerivedPropertyData();
        if (data is null)
        {
            return;
        }

        // Derived-with-setter: value comes from the getter, so setter changes require recalc
        // even when the getter recorded zero deps (e.g. short-circuited at attach).
        if (Volatile.Read(ref data.IsDerived) && context.Property.Metadata.SetValue is not null)
        {
            var rawTimestamp = context.WriteTimestampRaw;
            var storageTimestamp = rawTimestamp > 0 ? rawTimestamp : 0L;
            var property = context.Property;
            RecalculateDerivedProperty(ref property, storageTimestamp, rawTimestamp);
        }

        var usedByProperties = data.GetUsedByProperties();
        if (usedByProperties.Length > 0)
        {
            // Thread the trigger's resolved timestamp into each dependent's context, skipping a
            // scope push. storageTimestamp=0 under a null scope preserves the never-written sentinel.
            var rawTimestamp = context.WriteTimestampRaw;
            var storageTimestamp = rawTimestamp > 0 ? rawTimestamp : 0L;
            RecalculateDependents(usedByProperties, context.Property, storageTimestamp, rawTimestamp);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RecalculateDependents(ReadOnlySpan<PropertyReference> usedByProperties, PropertyReference triggerProperty, long storageTimestamp, long rawTimestamp)
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

            RecalculateDerivedProperty(ref dependent, storageTimestamp, rawTimestamp);
        }
    }

    /// <summary>
    /// Recalculates a derived property: re-evaluates getter, updates deps, fires change notification.
    /// The getter is evaluated OUTSIDE lock(data) to prevent deadlock with lock(_attachedSubjects)
    /// in LifecycleInterceptor when getters have side effects that write to subject-typed properties.
    /// IsRecalculating serializes concurrent recalculations; RecalculationNeeded catches state changes
    /// (writes, attach, detach) that occur during the unlocked evaluation window.
    /// </summary>
    internal static void RecalculateDerivedProperty(
        ref PropertyReference derivedProperty, long storageTimestamp, long rawTimestamp, bool onlyIfPendingAfterAttach = false)
    {
        // TODO(perf): Avoid boxing when possible (use TProperty generic parameter?)

        // Phase 1: Acquire recalculation ownership (brief lock).
        var data = TryBeginRecalculation(derivedProperty, onlyIfPendingAfterAttach);
        if (data is null)
        {
            return;
        }

        // Outer loop handles the post-notification RecalculationNeeded check without recursion,
        // preventing stack overflow under sustained concurrent writes.
        // The try-finally at this level ensures IsRecalculating is always cleared on exit.
        // Crucially, IsRecalculating stays true during NotifyDerivedPropertyChanged. This
        // serializes notification delivery with recalculation, preventing a stale notification
        // from being delivered after a newer one (TOCTOU race between guard checks and delivery).
        try
        {
            for (var outerIteration = 0; outerIteration < MaxStabilizationIterations; outerIteration++)
            {
                if (!TryEvaluateAndCommit(data, ref derivedProperty, storageTimestamp, out var oldValue, out var newValue, out var sequence))
                {
                    return;
                }

                // Any concurrent writes during delivery set RecalculationNeeded=true and bail out,
                // so no new recalculation (or notification) can start until delivery completes.
                NotifyDerivedPropertyChanged(ref derivedProperty, data, sequence, newValue, oldValue, rawTimestamp);

                // Uses a loop (not recursion) to prevent stack overflow under sustained concurrent writes.
                lock (data)
                {
                    if (!data.RecalculationNeeded || !data.IsAttached)
                    {
                        return;
                    }

                    data.RecalculationNeeded = false;
                }
            }

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
            // RecalculationNeeded is cleared before the re-trigger to prevent unbounded
            // recursion when the getter consistently throws: without clearing, the flag
            // persists (Phase 3 never runs to clear it), causing each re-trigger's
            // finally to re-trigger again indefinitely.
            bool needsRetrigger;
            lock (data)
            {
                needsRetrigger = data is { RecalculationNeeded: true, IsAttached: true };
                if (needsRetrigger)
                {
                    data.RecalculationNeeded = false;
                }

                data.IsRecalculating = false;
            }

            if (needsRetrigger)
            {
                RecalculateDerivedProperty(ref derivedProperty, storageTimestamp, rawTimestamp);
            }
        }
    }

    /// <summary>
    /// Takes recalculation ownership of an attached derived property. Returns null when there is
    /// nothing to recalculate or another recalculation owns it, which is then told to re-evaluate.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static DerivedPropertyData? TryBeginRecalculation(
        in PropertyReference derivedProperty, bool onlyIfPendingAfterAttach)
    {
        // TryGet and the IsDerived check: data removed by a detach, or recreated by a dependent's
        // registration, must not bring a detached derived property back from a stale used-by snapshot.
        var data = derivedProperty.TryGetDerivedPropertyData();
        if (data is null || !Volatile.Read(ref data.IsDerived))
        {
            return null;
        }

        lock (data)
        {
            // A recalculation since the attach, such as the cascade of the write itself or one on another thread,
            // has published the change already, or the property was attached again and that attach publishes it.
            if (onlyIfPendingAfterAttach && data.PublicationPendingAfterAttachThreadId != Environment.CurrentManagedThreadId)
            {
                return null;
            }

            if (data.IsRecalculating)
            {
                data.RecalculationNeeded = true;
                return null;
            }

            if (!data.IsAttached)
            {
                return null;
            }

            data.IsRecalculating = true;
            return data;
        }
    }

    /// <summary>
    /// Evaluates without holding lock(data), retries stale results, and commits the value and timestamp under lock.
    /// Requires recalculation ownership. Returns false if evaluation throws or the property is detached.
    /// </summary>
    private static bool TryEvaluateAndCommit(
        DerivedPropertyData data,
        ref PropertyReference derivedProperty,
        long storageTimestamp,
        out object? oldValue,
        out object? newValue,
        out long sequence)
    {
        oldValue = null;
        newValue = null;
        sequence = 0;

        while (true)
        {
            // Getter side effects can acquire lock(_attachedSubjects) in LifecycleInterceptor,
            // so holding lock(data) here would invert the lock order.
            try
            {
                newValue = EvaluateAndStabilize(data, derivedProperty, callerHoldsLock: false, out _);
            }
            catch (Exception)
            {
                // Getter threw. Keep LastKnownValue; a concurrent writer's cascade will retry.
                return false;
            }

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

                // Read here and not when the recalculation began: an attach during the evaluation has replaced it.
                oldValue = data.LastKnownValue;
                data.LastKnownValue = newValue;
                data.PublicationPendingAfterAttachThreadId = 0;
                sequence = ++data.RecalculationSequence;
                derivedProperty.SetWriteTimestamp(storageTimestamp);
                return true;
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
        // oldValue by the guards above, so finalValueIsNewValue publishes it rather than
        // re-invoking the getter (see PropertyWriteContext.FinalValueIsNewValue).
        derivedProperty.PublishDerivedPropertyValue(newValue, oldValue, rawTimestamp, finalValueIsNewValue: true);

        if (derivedProperty.Subject is IRaisePropertyChanged raiser)
        {
            raiser.RaisePropertyChanged(derivedProperty.Metadata.Name);
        }
    }

    /// <summary>
    /// Evaluates a derived property getter, records dependencies, and runs the stabilization
    /// loop if concurrent writes changed the dependency set.
    /// When <paramref name="callerHoldsLock"/> is true (the evaluation at attach), the caller already holds
    /// lock(data) and lock(_attachedSubjects), so UpdateDependencies runs directly.
    /// When false (RecalculateDerivedProperty and the evaluation at the end of an attach), the lock is acquired only briefly for
    /// UpdateDependencies, preventing deadlock between lock(data) and lock(_attachedSubjects)
    /// when getters have side effects that write to subject-typed properties.
    /// </summary>
    private static object? EvaluateAndStabilize(
        DerivedPropertyData data, in PropertyReference property, bool callerHoldsLock, out bool dependenciesChanged)
    {
        dependenciesChanged = false;
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
            dependenciesChanged = dependencyUpdate == DependencyUpdate.Changed;
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
            // The getter detached its own property, which dropped its dependencies: recording them again would keep
            // the detached property in their used-by properties.
            if (!data.IsAttached)
            {
                _recorder!.ClearLastRecording();
                return DependencyUpdate.Abandoned;
            }

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
