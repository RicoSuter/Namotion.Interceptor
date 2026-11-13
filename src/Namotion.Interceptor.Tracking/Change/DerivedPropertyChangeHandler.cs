using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Handles derived property tracking and automatic recalculation using dependency recording.
/// A write requests the recalculation of its dependents once, after the whole write chain.
/// </summary>
/// <remarks>
/// Deadlock safety: a getter and a change publication never run under the per-property lock of
/// <see cref="DerivedPropertyData"/>, and no other property's lock is taken inside it; only the used-by
/// collection's leaf lock is. One owner at a time evaluates and publishes a derived property; a request
/// that finds it owned is left to that owner.
/// See docs/design/tracking-derived-properties.md for full concurrency analysis.
/// </remarks>
public class DerivedPropertyChangeHandler : IPropertyLifecycleHandler
{
    // Safety limit for stabilization loops. Prevents infinite loops from getters
    // with side effects that mutate the tracked state (a user error but shouldn't hang).
    // In correct code, the loop runs 1-2 iterations max.
    private const int MaxStabilizationIterations = 100;

    internal static bool IsRecordingDerivedProperty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => DerivedPropertyRecorder.IsEvaluatingOnThisThread;
    }

    public DerivedPropertyChangeHandler()
    {
        TrackingWriteObserver.EnsureRegistered();
    }

    /// <inheritdoc />
    public void AttachProperty(SubjectPropertyLifecycleChange change)
    {
        if (!change.Property.Metadata.IsDerived)
        {
            return;
        }

        var property = change.Property;
        var data = property.GetDerivedPropertyData();
        long attachToken;
        bool ownsData;
        lock (data)
        {
            // Attached through another graph already: it has its value and its links.
            if (data.IsAttached)
            {
                return;
            }

            data.IsAttached = true;
            Volatile.Write(ref data.IsDerived, true);
            data.HasValue = false;
            data.IsFaulted = false;
            attachToken = data.AttachToken = DerivedPropertyData.NewToken();
            ownsData = data.OwnerToken == 0;
            if (ownsData)
            {
                // Requests on a detached property are dropped, so a flag left from before the detach is stale.
                data.OwnerToken = attachToken;
                data.RecalculationNeeded = false;
                data.PendingRawTimestamp = 0;
            }
            else
            {
                // A recalculation from before a detach is in flight: it hands off at its next lock. A pending
                // timestamp is from before the detach, so the request is stamped when it runs.
                data.RecalculationNeeded = true;
                data.PendingRawTimestamp = 0;
            }
        }

        EvaluateAtAttach(data, property, attachToken, ownsData);
    }

    /// <inheritdoc />
    /// <remarks>
    /// A detach issued by a lifecycle interceptor is ignored: the subject may still be attached to
    /// another graph, and the lifecycle detaches the derived properties itself once the subject left
    /// its last graph (<see cref="DetachDerivedProperties"/>). A direct call detaches, unless it is made on a
    /// thread on which a lifecycle interceptor is detaching a subject's properties (for example by a lifecycle
    /// handler): such a call is ignored, whichever subject it is for.
    /// </remarks>
    public void DetachProperty(SubjectPropertyLifecycleChange change)
    {
        if (!SubjectLifecycleAttachments.IsLifecycleDetach && change.Property.Metadata.IsDerived)
        {
            DetachDerivedProperty(change.Property);
        }
    }

    /// <summary>Detaches every derived property of a subject that left its last lifecycle interceptor.</summary>
    internal static void DetachDerivedProperties(IInterceptorSubject subject)
    {
        foreach (var entry in subject.Properties)
        {
            if (entry.Value.IsDerived)
            {
                DetachDerivedProperty(new PropertyReference(subject, entry.Key), onlyIfSubjectIsDetached: true);
            }
        }
    }

    // Called for a derived property only.
    private static void DetachDerivedProperty(in PropertyReference property, bool onlyIfSubjectIsDetached = false)
    {
        var data = property.TryGetDerivedPropertyData();
        if (data is null)
        {
            return;
        }

        lock (data)
        {
            // Decided under the lock that AttachProperty takes as well: an interceptor that attached the
            // subject after the caller's check keeps the property attached.
            if (!data.IsAttached || (onlyIfSubjectIsDetached && SubjectLifecycleAttachments.IsAttached(property.Subject)))
            {
                return;
            }

            data.IsAttached = false;
            data.AttachToken = 0;
            data.RemoveAllLinks(property);
            data.LastKnownValue = null;
            data.HasValue = false;
            data.IsFaulted = false;

            // Fails the stale-publication guard of a publication already past its commit, also for a null value.
            data.RecalculationSequence++;
        }
    }

    /// <summary>
    /// Requests the recalculation of a derived property after a write to one of its dependencies (or to the
    /// property itself, for a derived property with a setter). A <paramref name="rawTimestamp"/> of 0 resolves
    /// the current changed timestamp. The request is dropped for a detached property, and left to the current
    /// owner or attach when there is one; otherwise this call owns the property until no request is pending.
    /// </summary>
    /// <param name="property">The derived property.</param>
    /// <param name="rawTimestamp">The raw changed timestamp of the trigger.</param>
    /// <param name="isHandOver">The request carries its own trigger timestamp (handed over by an owner or an attach).</param>
    internal static void RequestRecalculation(PropertyReference property, long rawTimestamp, bool isHandOver = false)
    {
        if (rawTimestamp == 0)
        {
            // A write without a context or a structure change: the time of the request is the trigger's.
            rawTimestamp = SubjectChangeContext.Current.ResolveChangedTimestamp();
        }

        // The IsDerived check: data that a dependent's recording created before this property's attach
        // must not start a recalculation.
        var data = property.TryGetDerivedPropertyData();
        if (data is null || !Volatile.Read(ref data.IsDerived))
        {
            return;
        }

        long token;
        lock (data)
        {
            if (!data.IsAttached)
            {
                return;
            }

            if (data.OwnerToken != 0 || data.AttachToken != 0)
            {
                data.RecalculationNeeded = true;
                data.PendingRawTimestamp = rawTimestamp;
                return;
            }

            token = data.OwnerToken = DerivedPropertyData.NewToken();
        }

        RunOwnerLoops(data, property, token, rawTimestamp);
    }

    /// <summary>
    /// Evaluates the getter at attach, outside lock(data), and commits the first value without publishing it.
    /// The attach token decides whether the commit counts: a newer attach or a detach supersedes it.
    /// </summary>
    private static void EvaluateAtAttach(DerivedPropertyData data, PropertyReference property, long attachToken, bool ownsData)
    {
        bool requestAfterAttach;
        long pendingTimestamp;
        try
        {
            requestAfterAttach = RunAttachEvaluations(data, property, attachToken, ownsData, out pendingTimestamp);
        }
        catch (Exception)
        {
            // Something besides the getter threw (a trace listener, a timestamp): the attach must not keep the
            // property owned, or every later request would be left to it.
            if (ReleaseAttachAfterFailure(data, attachToken, out pendingTimestamp))
            {
                RequestRecalculationAfterAttachFailure(property, pendingTimestamp);
            }

            throw;
        }

        if (requestAfterAttach)
        {
            RequestRecalculation(property, pendingTimestamp, isHandOver: true);
        }
    }

    // Returns true when a request is pending and nobody owns the property after the attach.
    private static bool RunAttachEvaluations(
        DerivedPropertyData data, PropertyReference property, long attachToken, bool ownsData, out long pendingTimestamp)
    {
        var requestAfterAttach = false;
        pendingTimestamp = 0;

        // The last iteration always commits, so the loop ends by a break.
        for (var iteration = 0; iteration < MaxStabilizationIterations; iteration++)
        {
            var evaluation = Evaluate(property);
            var isLastIteration = iteration == MaxStabilizationIterations - 1;
            lock (data)
            {
                if (data.AttachToken != attachToken)
                {
                    // Superseded by a newer attach or a detach (also by the getter detaching its own subject).
                    DiscardRecording();
                    if (ownsData && data.OwnerToken == attachToken)
                    {
                        data.OwnerToken = 0;
                    }

                    requestAfterAttach = TakePendingRequestIfUnowned(data, out pendingTimestamp);
                    break;
                }

                var isStable = CommitAttachLinks(data, property, ownsData);
                if (!isStable && !isLastIteration)
                {
                    continue;
                }

                CommitAttachEvaluation(data, property, evaluation, isStable, attachToken, ownsData);
                requestAfterAttach = TakePendingRequestIfUnowned(data, out pendingTimestamp);
                break;
            }
        }

        return requestAfterAttach;
    }

    // Called under lock(data). Sets the links and returns whether the evaluation is stable. When the attach owns
    // the property, it takes a request that arrived during the evaluation and answers it by evaluating again;
    // otherwise the flag belongs to the owner in flight.
    private static bool CommitAttachLinks(DerivedPropertyData data, in PropertyReference property, bool ownsData)
    {
        var isStable = CommitLinks(data, property);
        if (ownsData && data.RecalculationNeeded)
        {
            data.RecalculationNeeded = false;
            data.PendingRawTimestamp = 0;
            isStable = false;
        }

        return isStable;
    }

    // Called under lock(data) with a current attach token, after the last evaluation of the attach.
    private static void CommitAttachEvaluation(
        DerivedPropertyData data, in PropertyReference property, Evaluation evaluation, bool isStable, long attachToken, bool ownsData)
    {
        if (!isStable)
        {
            // The getter keeps changing what it reads (for example it writes a property it reads): the last
            // value is committed, and a recalculation is requested after the attach.
            Trace.TraceWarning(
                $"DerivedPropertyChangeHandler: MaxStabilizationIterations ({MaxStabilizationIterations}) exhausted " +
                $"while attaching '{property.Metadata.Name}' on {property.Subject.GetType().Name}.");
            data.RecalculationNeeded = true;
        }

        CommitFirstValue(data, property, evaluation);
        data.AttachToken = 0;
        if (ownsData && data.OwnerToken == attachToken)
        {
            data.OwnerToken = 0;
        }
    }

    // Ends a failed attach if it is still this one and releases the ownership it holds. Returns true when a
    // request is pending and nobody owns the property.
    private static bool ReleaseAttachAfterFailure(DerivedPropertyData data, long attachToken, out long pendingTimestamp)
    {
        DiscardRecording();
        lock (data)
        {
            if (data.AttachToken == attachToken)
            {
                data.AttachToken = 0;
            }

            if (data.OwnerToken == attachToken)
            {
                data.OwnerToken = 0;
            }

            return TakePendingRequestIfUnowned(data, out pendingTimestamp);
        }
    }

    private static void RequestRecalculationAfterAttachFailure(PropertyReference property, long pendingTimestamp)
    {
        try
        {
            RequestRecalculation(property, pendingTimestamp, isHandOver: true);
        }
        catch (Exception)
        {
            // The attach's exception propagates; a later one is dropped, as in RunOwnerLoops.
        }
    }

    // Called under lock(data): a request that an in-flight owner handed off to the attach.
    private static bool TakePendingRequestIfUnowned(DerivedPropertyData data, out long pendingTimestamp)
    {
        pendingTimestamp = 0;
        if (data.OwnerToken != 0 || data.AttachToken != 0 || !data.IsAttached || !data.RecalculationNeeded)
        {
            return false;
        }

        pendingTimestamp = TakePendingTimestamp(data);
        data.RecalculationNeeded = false;
        return true;
    }

    // Called under lock(data). Zeroed when taken, so a flag set without a request (by an attach) does
    // not carry an older write's timestamp; 0 resolves to the current time in RequestRecalculation.
    private static long TakePendingTimestamp(DerivedPropertyData data)
    {
        var timestamp = data.PendingRawTimestamp;
        data.PendingRawTimestamp = 0;
        return timestamp;
    }

    // Called under lock(data) by an owner that adopts a pending request instead of passing it to RequestRecalculation.
    private static long AdoptPendingTimestamp(DerivedPropertyData data)
    {
        var timestamp = TakePendingTimestamp(data);
        return timestamp != 0 ? timestamp : SubjectChangeContext.Current.ResolveChangedTimestamp();
    }

    // Called under lock(data). The first value is not published: consumers read it while the subject attaches.
    private static void CommitFirstValue(DerivedPropertyData data, in PropertyReference property, Evaluation evaluation)
    {
        if (evaluation.HasThrown)
        {
            data.IsFaulted = true;
            data.HasValue = false;
            return;
        }

        data.LastKnownValue = evaluation.Value;
        data.HasValue = true;
        data.IsFaulted = false;
        data.RecalculationSequence++;
        property.SetWriteTimestamp(SubjectChangeContext.Current.ResolveChangedTimestamp());
    }

    /// <summary>
    /// Runs owner loops in this frame until no request is pending. After a failure (an observer threw, or the
    /// iteration limit was reached) the loop releases ownership, and a pending request is taken over by a new
    /// loop in this frame instead of a nested one. After an exception at most one further loop runs, so an
    /// observer that always throws and writes a dependency ends; the first exception propagates.
    /// </summary>
    private static void RunOwnerLoops(DerivedPropertyData data, PropertyReference property, long token, long rawTimestamp)
    {
        ExceptionDispatchInfo? firstException = null;
        while (true)
        {
            var isFurtherLoopAfterException = firstException is not null;
            try
            {
                if (RunOwnerLoop(data, property, token, ref rawTimestamp))
                {
                    break;
                }
            }
            catch (Exception exception)
            {
                firstException ??= ExceptionDispatchInfo.Capture(exception);
            }

            if (!TryTakeOverAfterFailure(data, ref token, ref rawTimestamp, isTakeOverAllowed: !isFurtherLoopAfterException))
            {
                break;
            }
        }

        firstException?.Throw();
    }

    // Returns true when the loop released ownership, false when the iteration limit was reached while it owns the property.
    private static bool RunOwnerLoop(DerivedPropertyData data, PropertyReference property, long token, ref long rawTimestamp)
    {
        for (var iteration = 0; iteration < MaxStabilizationIterations; iteration++)
        {
            if (RunOwnerIteration(data, property, token, ref rawTimestamp) == OwnerStep.Released)
            {
                return true;
            }
        }

        Trace.TraceWarning(
            $"DerivedPropertyChangeHandler: MaxStabilizationIterations ({MaxStabilizationIterations}) exhausted for " +
            $"'{property.Metadata.Name}' on {property.Subject.GetType().Name}.");
        return false;
    }

    private enum OwnerStep
    {
        Continue,
        Released
    }

    /// <summary>
    /// One evaluation of the owner loop: evaluates and commits, publishes outside the lock while still owning
    /// the property, then continues with a pending request or releases ownership.
    /// </summary>
    private static OwnerStep RunOwnerIteration(DerivedPropertyData data, PropertyReference property, long token, ref long rawTimestamp)
    {
        var commit = EvaluateAndCommit(data, property, token, ref rawTimestamp);
        if (commit.Outcome != CommitOutcome.Committed)
        {
            return commit.Outcome == CommitOutcome.EvaluateAgain ? OwnerStep.Continue : OwnerStep.Released;
        }

        if (commit.ShouldPublish)
        {
            NotifyDerivedPropertyChanged(property, data, commit.Sequence, commit.NewValue, commit.OldValue, rawTimestamp);
        }

        lock (data)
        {
            if (data.OwnerToken != token)
            {
                return OwnerStep.Released;
            }

            if (data.RecalculationNeeded && data.IsAttached && data.AttachToken == 0)
            {
                data.RecalculationNeeded = false;
                rawTimestamp = AdoptPendingTimestamp(data);
                return OwnerStep.Continue;
            }

            // Detached, or an attach is in progress, which takes over a pending request (RecalculationNeeded stays).
            data.OwnerToken = 0;
            return OwnerStep.Released;
        }
    }

    // Called after a failed owner loop: releases ownership and, when allowed, takes a pending request over with
    // a new token. A pending request that may not be taken over is dropped.
    private static bool TryTakeOverAfterFailure(DerivedPropertyData data, ref long token, ref long rawTimestamp, bool isTakeOverAllowed)
    {
        lock (data)
        {
            if (data.OwnerToken != token)
            {
                return false;
            }

            data.OwnerToken = 0;
            if (!data.RecalculationNeeded || !data.IsAttached || data.AttachToken != 0)
            {
                return false;
            }

            data.RecalculationNeeded = false;
            if (!isTakeOverAllowed)
            {
                data.PendingRawTimestamp = 0;
                return false;
            }

            rawTimestamp = AdoptPendingTimestamp(data);
            token = data.OwnerToken = DerivedPropertyData.NewToken();
            return true;
        }
    }

    private enum CommitOutcome
    {
        Committed,
        EvaluateAgain,
        Released
    }

    private readonly record struct Commit(
        CommitOutcome Outcome, bool ShouldPublish = false, object? OldValue = null, object? NewValue = null, long Sequence = 0);

    /// <summary>Evaluates outside lock(data) and commits under it.</summary>
    private static Commit EvaluateAndCommit(DerivedPropertyData data, PropertyReference property, long token, ref long rawTimestamp)
    {
        var evaluation = Evaluate(property);
        lock (data)
        {
            if (data.OwnerToken != token)
            {
                DiscardRecording();
                return new Commit(CommitOutcome.Released);
            }

            if (!data.IsAttached || data.AttachToken != 0)
            {
                // Detached, or an attach is in progress and takes over (RecalculationNeeded stays).
                DiscardRecording();
                data.OwnerToken = 0;
                return new Commit(CommitOutcome.Released);
            }

            if (data.RecalculationNeeded)
            {
                DiscardRecording();
                data.RecalculationNeeded = false;
                rawTimestamp = AdoptPendingTimestamp(data);
                return new Commit(CommitOutcome.EvaluateAgain);
            }

            if (!CommitLinks(data, property))
            {
                return new Commit(CommitOutcome.EvaluateAgain);
            }

            if (evaluation.HasThrown)
            {
                // The previous value stays; the first success afterwards is published (IsFaulted). No request is
                // pending: one would have been taken above.
                data.IsFaulted = true;
                data.OwnerToken = 0;
                return new Commit(CommitOutcome.Released);
            }

            return CommitValue(data, property, evaluation.Value, rawTimestamp);
        }
    }

    // Called under lock(data) by the owner. The old value is the last committed one, or the type's default
    // when there is none (the attach evaluation threw); only the attach's first value is not published.
    private static Commit CommitValue(DerivedPropertyData data, in PropertyReference property, object? newValue, long rawTimestamp)
    {
        var hadValue = data.HasValue;
        var oldValue = hadValue ? data.LastKnownValue : GetDefault(property.Metadata.Type);
        var shouldPublish = hadValue || data.IsFaulted;
        data.LastKnownValue = newValue;
        data.HasValue = true;
        data.IsFaulted = false;
        var sequence = ++data.RecalculationSequence;
        property.SetWriteTimestamp(rawTimestamp > 0 ? rawTimestamp : 0L);
        return new Commit(CommitOutcome.Committed, ShouldPublish: shouldPublish, OldValue: oldValue, NewValue: newValue, Sequence: sequence);
    }

    private static object? GetDefault(Type type) => type.IsValueType ? Activator.CreateInstance(type) : null;

    /// <summary>
    /// Fires change notification for a recalculated derived property.
    /// Called outside lock(data) to avoid deadlock with lock(_attachedSubjects) in LifecycleInterceptor.
    /// Skips if a newer recalculation already completed (stale sequence or overwritten value).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void NotifyDerivedPropertyChanged(
        PropertyReference derivedProperty,
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
        // newValue is the value the owner committed and the value paired with oldValue by the
        // guards above, so finalValueIsNewValue publishes it rather than re-invoking the getter
        // (see PropertyWriteContext.FinalValueIsNewValue).
        derivedProperty.PublishDerivedPropertyValue(newValue, oldValue, rawTimestamp, finalValueIsNewValue: true);

        if (derivedProperty.Subject is IRaisePropertyChanged raiser)
        {
            raiser.RaisePropertyChanged(derivedProperty.Metadata.Name);
        }
    }

    private readonly struct Evaluation(object? value, bool hasThrown)
    {
        public object? Value { get; } = value;

        public bool HasThrown { get; } = hasThrown;
    }

    /// <summary>
    /// Runs the getter outside every lock and records what it read; the recording stays on the
    /// recorder until <see cref="CommitLinks"/> or <see cref="DiscardRecording"/> clears it.
    /// </summary>
    private static Evaluation Evaluate(in PropertyReference property)
    {
        var recorder = DerivedPropertyRecorder.Current;
        recorder.StartRecording(property);
        try
        {
            return new Evaluation(property.Metadata.GetValue?.Invoke(property.Subject), hasThrown: false);
        }
        catch (Exception)
        {
            // The reads before the throw are the ones that decided it; they are kept as dependencies.
            return new Evaluation(null, hasThrown: true);
        }
        finally
        {
            recorder.FinishRecording();
        }
    }

    /// <summary>
    /// Sets the links to the recorded reads and reports whether no source moved since it was read.
    /// Called under lock(data).
    /// </summary>
    private static bool CommitLinks(DerivedPropertyData data, in PropertyReference property)
    {
        var recorder = DerivedPropertyRecorder.Current;
        try
        {
            var recorded = recorder.LastRecording;
            data.UpdateDependencies(property, recorded);
            return !HasVersionChanged(recorded);
        }
        finally
        {
            recorder.ClearLastRecording();
        }
    }

    private static void DiscardRecording() => DerivedPropertyRecorder.Current.ClearLastRecording();

    private static bool HasVersionChanged(ReadOnlySpan<RecordedDependency> recorded)
    {
        foreach (ref readonly var dependency in recorded)
        {
            // R5: a full fence, paired with the writer's Interlocked increment before it reads the used-by set.
            if (Interlocked.Read(ref dependency.Data.Version) != dependency.Version)
            {
                return true;
            }
        }

        return false;
    }
}
