using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// The ownership protocol of derived properties: a request, the owner loop that evaluates, commits and publishes,
/// the attach evaluation, and the takeover after a failure.
/// </summary>
/// <remarks>
/// Deadlock safety: a getter and a change publication never run under the per-property lock of
/// <see cref="DerivedPropertyData"/>, and no other property's lock is taken inside it; only the used-by
/// collection's leaf lock is. One owner at a time evaluates and publishes a derived property; a request
/// that finds it owned is left to that owner.
/// See docs/design/tracking-derived-properties.md for full concurrency analysis.
/// </remarks>
internal static class DerivedPropertyOwnership
{
    // Safety limit for stabilization loops. Prevents infinite loops from getters
    // with side effects that mutate the tracked state (a user error but shouldn't hang).
    // In correct code, the loop runs 1-2 iterations max.
    private const int MaxStabilizationIterations = 100;

    /// <summary>
    /// The attach count of a request raised by a write or by <c>RecalculateDerivedProperty</c>: it is not checked when
    /// the request runs at once, and a queued request takes the property's attach count when it is queued.
    /// </summary>
    internal const long AnyAttachCount = -1;

    /// <summary>
    /// Runs, when a test sets it, after an owner or an attach took a request under lock(data) and before the request
    /// is evaluated, queued or run.
    /// </summary>
    internal static Action<PropertyReference>? AfterTakeForTests { get; set; }

    /// <summary>
    /// Attaches a derived property and evaluates its first value, unless it is attached through another graph
    /// already.
    /// </summary>
    internal static void Attach(PropertyReference property)
    {
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
            Volatile.Write(ref data.AttachCount, data.AttachCount + 1);

            // Requests on a detached property are dropped, so a flag left from before the detach is stale. A
            // recalculation from before the detach that is still in flight is not signaled: it releases the
            // property at its commit (EvaluateAndCommit), and the attach requests a recalculation after its own.
            TakePendingRequest(data, out _);
            ownsData = data.OwnerToken == 0;
            if (ownsData)
            {
                TakeOwnership(data, attachToken);
            }
        }

        EvaluateAtAttach(data, property, attachToken, ownsData);
    }

    /// <summary>
    /// Detaches a derived property: removes its links, clears its value and makes an evaluation in flight
    /// discard its result. With <paramref name="onlyIfSubjectIsDetached"/>, a subject that is attached again keeps
    /// the property attached.
    /// </summary>
    internal static void Detach(in PropertyReference property, bool onlyIfSubjectIsDetached)
    {
        var data = property.TryGetDerivedPropertyData();
        if (data is null)
        {
            return;
        }

        lock (data)
        {
            // Decided under the lock that Attach takes as well: an interceptor that attached the
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
    /// owner or attach when there is one; otherwise this call owns the property until no request is pending. While
    /// the thread holds a lifecycle lock, the request is queued with its timestamp and runs after the lifecycle
    /// operation (<see cref="PostLockQueue"/>).
    /// </summary>
    /// <param name="property">The derived property.</param>
    /// <param name="rawTimestamp">The raw changed timestamp of the trigger.</param>
    internal static void RequestRecalculation(PropertyReference property, long rawTimestamp)
    {
        if (!PostLockQueue.TryEnqueueRequest(property, rawTimestamp, AnyAttachCount))
        {
            Request(property, rawTimestamp, AnyAttachCount);
        }
    }

    /// <summary>
    /// Runs a request that was queued under a lifecycle lock, outside every lifecycle lock. It is dropped when the
    /// property was attached again since it was queued (<paramref name="attachCount"/> moved): that attach evaluated
    /// after the request's cause.
    /// </summary>
    internal static void RunQueuedRequest(PropertyReference property, long rawTimestamp, long attachCount)
    {
        Request(property, rawTimestamp, attachCount);
    }

    private static void Request(PropertyReference property, long rawTimestamp, long attachCount)
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
        long ownerAttachCount;
        lock (data)
        {
            if (!data.IsAttached || (attachCount != AnyAttachCount && data.AttachCount != attachCount))
            {
                return;
            }

            if (data.OwnerToken != 0 || data.AttachToken != 0)
            {
                MarkRecalculationNeeded(data);
                data.PendingRawTimestamp = rawTimestamp;
                return;
            }

            token = TakeOwnership(data, DerivedPropertyData.NewToken());
            ownerAttachCount = data.AttachCount;
        }

        RunOwnerLoops(data, property, token, rawTimestamp, ownerAttachCount);
    }

    /// <summary>Traces an exception that is dropped because another one wins or nobody can receive it.</summary>
    internal static void TraceDropped(string reason, Exception exception)
    {
        TraceWarning($"DerivedPropertyChangeHandler: {reason}", exception);
    }

    /// <summary>
    /// Rethrows the first exception of work that ran on after it, or traces it when <paramref name="rethrow"/> is
    /// false because another exception is in flight or the caller must not throw.
    /// </summary>
    internal static void ThrowOrTrace(ExceptionDispatchInfo? exception, bool rethrow, string reason)
    {
        if (exception is null)
        {
            return;
        }

        if (rethrow)
        {
            exception.Throw();
        }

        TraceDropped(reason, exception.SourceException);
    }

    private static void TraceWarning(string message, Exception? exception = null)
    {
        try
        {
            Trace.TraceWarning(exception is null ? message : $"{message}: {exception}");
        }
        catch (Exception)
        {
            // A failing trace listener, or an exception whose formatting throws, must not replace an exception in
            // flight or abort an attach.
        }
    }

    // Called under lock(data).
    private static long TakeOwnership(DerivedPropertyData data, long token)
    {
        data.OwnerToken = token;
        data.OwnerThreadId = Environment.CurrentManagedThreadId;
        return token;
    }

    // Called under lock(data) for a request while the property is owned or an attach is in progress. A request
    // raised on another thread than the owner's is foreign, and a foreign request is never dropped.
    private static void MarkRecalculationNeeded(DerivedPropertyData data)
    {
        data.RecalculationNeeded = true;
        if (data.OwnerToken == 0 || data.OwnerThreadId != Environment.CurrentManagedThreadId)
        {
            data.HasForeignRequest = true;
        }
    }

    // Called under lock(data). Clears the pending request flags and returns whether a foreign request was among them.
    // The pending timestamp is zeroed when taken, so a flag set without a request (by an attach) does not carry an
    // older write's timestamp; 0 is resolved to the current time outside the lock (by RequestRecalculation, or by the
    // publication of the owner loop that takes it).
    private static bool TakePendingRequest(DerivedPropertyData data, out long pendingTimestamp)
    {
        var isForeign = data.HasForeignRequest;
        pendingTimestamp = data.PendingRawTimestamp;
        data.RecalculationNeeded = false;
        data.HasForeignRequest = false;
        data.PendingRawTimestamp = 0;
        return isForeign;
    }

    /// <summary>
    /// Evaluates the getter at attach, outside lock(data), and commits the first value without publishing it.
    /// The attach token decides whether the commit counts: a newer attach or a detach supersedes it. Never throws:
    /// an exception is traced, so the remaining properties and lifecycle handlers of the subject still attach.
    /// </summary>
    private static void EvaluateAtAttach(DerivedPropertyData data, PropertyReference property, long attachToken, bool ownsData)
    {
        bool requestAfterAttach;
        long pendingTimestamp;
        long attachCount;
        bool isExhausted;
        try
        {
            requestAfterAttach = RunAttachEvaluations(
                data, property, attachToken, ownsData, out pendingTimestamp, out attachCount, out isExhausted);
        }
        catch (Exception exception)
        {
            // Something besides the getter threw (a time source): the attach must not keep the property owned, or
            // every later request would be left to it.
            if (ReleaseAttachAfterFailure(data, attachToken, out pendingTimestamp, out attachCount))
            {
                RequestRecalculationAfterAttach(property, pendingTimestamp, attachCount);
            }

            TraceDropped($"the attach of '{property.Metadata.Name}' on {property.Subject.GetType().Name} failed", exception);
            return;
        }

        if (requestAfterAttach)
        {
            RequestRecalculationAfterAttach(property, pendingTimestamp, attachCount);
        }

        // Traced outside lock(data), and after the request, so a failing trace listener cannot lose it.
        if (isExhausted)
        {
            TraceWarning(
                $"DerivedPropertyChangeHandler: MaxStabilizationIterations ({MaxStabilizationIterations}) exhausted " +
                $"while attaching '{property.Metadata.Name}' on {property.Subject.GetType().Name}.");
        }
    }

    // The recalculation publishes on the attaching thread: an observer's exception is traced, and the recalculation
    // has handled the requests it left (see RunOwnerLoops).
    private static void RequestRecalculationAfterAttach(PropertyReference property, long pendingTimestamp, long attachCount)
    {
        try
        {
            AfterTakeForTests?.Invoke(property);
            if (!PostLockQueue.TryEnqueueRequest(property, pendingTimestamp, attachCount))
            {
                Request(property, pendingTimestamp, attachCount);
            }
        }
        catch (Exception exception)
        {
            TraceDropped($"the recalculation after the attach of '{property.Metadata.Name}' on {property.Subject.GetType().Name} threw", exception);
        }
    }

    // Returns true when a request is pending and nobody owns the property after the attach. attachCount: the attach
    // count under lock(data) when the attach decided the request, which the request carries: an attach that increments
    // it later evaluates after every cause of the request. isExhausted: the iteration limit was reached and the last,
    // unstable evaluation was committed.
    private static bool RunAttachEvaluations(
        DerivedPropertyData data, PropertyReference property, long attachToken, bool ownsData, out long pendingTimestamp,
        out long attachCount, out bool isExhausted)
    {
        var requestAfterAttach = false;
        var hasEvaluatedAfterOwnWrite = false;
        pendingTimestamp = 0;
        attachCount = 0;
        isExhausted = false;

        // The last iteration always commits, so the loop ends by a break.
        for (var iteration = 0; iteration < MaxStabilizationIterations; iteration++)
        {
            // The time source is user code: resolved outside lock(data), and discarded unless this iteration
            // commits. Resolved before the evaluation, since a time source that writes can run another
            // evaluation on this thread, which would replace the recording this evaluation leaves until its commit.
            var timestamp = SubjectChangeContext.Current.ResolveChangedTimestamp();
            var evaluation = Evaluate(data, property);
            var isLastIteration = iteration == MaxStabilizationIterations - 1;
            lock (data)
            {
                if (data.AttachToken != attachToken)
                {
                    // Superseded by a newer attach or a detach (also by the getter detaching its own subject).
                    DiscardRecording();
                    if (data.OwnerToken == attachToken)
                    {
                        data.OwnerToken = 0;
                    }

                    requestAfterAttach = TakePendingRequestIfUnowned(data, out pendingTimestamp);
                    attachCount = data.AttachCount;
                    break;
                }

                var isStable = CommitAttachLinks(data, property, ownsData, ref hasEvaluatedAfterOwnWrite);
                if (!isStable && !isLastIteration)
                {
                    continue;
                }

                CommitAttachEvaluation(data, property, evaluation, attachToken, timestamp);
                isExhausted = !isStable;

                // An unstable evaluation is evaluated again after the attach. So is one that found a recalculation
                // from before the detach in flight, which this request hands over to, with the attach's timestamp
                // unless a request that arrived since carries its own. Under the lifecycle lock the request is
                // queued, so it does not run before the structural change is announced.
                requestAfterAttach = TakePendingRequestIfUnowned(data, out pendingTimestamp) || !isStable || !ownsData;
                if (!ownsData && pendingTimestamp == 0)
                {
                    pendingTimestamp = timestamp;
                }

                attachCount = data.AttachCount;
                break;
            }
        }

        return requestAfterAttach;
    }

    // Called under lock(data). Sets the links and returns whether the evaluation is stable. When the attach owns
    // the property, it takes a request that arrived during the evaluation and answers it by evaluating again;
    // otherwise the flag belongs to the owner in flight.
    private static bool CommitAttachLinks(
        DerivedPropertyData data, in PropertyReference property, bool ownsData, ref bool hasEvaluatedAfterOwnWrite)
    {
        var isStable = IsStable(CommitLinks(data, property), ref hasEvaluatedAfterOwnWrite);
        if (ownsData && data.RecalculationNeeded)
        {
            TakePendingRequest(data, out _);
            isStable = false;
        }

        return isStable;
    }

    // Called under lock(data) with a current attach token, after the last evaluation of the attach. An unstable
    // evaluation (the getter keeps changing what it reads, for example it writes a property it reads) is committed
    // as well, and the attach requests a recalculation after it.
    private static void CommitAttachEvaluation(
        DerivedPropertyData data, in PropertyReference property, Evaluation evaluation, long attachToken, long timestamp)
    {
        CommitFirstValue(data, property, evaluation, timestamp);
        data.AttachToken = 0;
        if (data.OwnerToken == attachToken)
        {
            data.OwnerToken = 0;
        }
    }

    // Ends a failed attach if it is still this one and releases the ownership it holds. Returns true when a
    // recalculation is to be requested after the attach: always for the current attach, otherwise when a request
    // is pending and nobody owns the property.
    private static bool ReleaseAttachAfterFailure(DerivedPropertyData data, long attachToken, out long pendingTimestamp, out long attachCount)
    {
        DiscardRecording();
        lock (data)
        {
            if (data.OwnerToken == attachToken)
            {
                data.OwnerToken = 0;
            }

            attachCount = data.AttachCount;
            if (data.AttachToken != attachToken)
            {
                return TakePendingRequestIfUnowned(data, out pendingTimestamp);
            }

            // Failed before its first value and maybe its links were committed: a recalculation after the failure
            // evaluates again, and its success is published, as after a getter that threw.
            data.AttachToken = 0;
            if (!data.HasValue)
            {
                data.IsFaulted = true;
            }

            TakePendingRequest(data, out pendingTimestamp);
            return true;
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

        TakePendingRequest(data, out pendingTimestamp);
        return true;
    }

    // Called under lock(data). The first value is not published: consumers read it while the subject attaches.
    private static void CommitFirstValue(DerivedPropertyData data, in PropertyReference property, Evaluation evaluation, long timestamp)
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
        property.SetWriteTimestamp(timestamp);
    }

    /// <summary>
    /// Runs owner loops in this frame until no request is pending, taking a request over after a failed loop
    /// ("Failures and takeover" in docs/design/tracking-derived-properties.md). The first exception propagates.
    /// </summary>
    private static void RunOwnerLoops(DerivedPropertyData data, PropertyReference property, long token, long rawTimestamp, long attachCount)
    {
        ExceptionDispatchInfo? firstException = null;
        var state = new OwnerLoopState { RawTimestamp = rawTimestamp, AttachCount = attachCount };
        while (true)
        {
            var isFirstException = false;
            state.Unanswered = UnansweredRequest.None;
            state.HasEvaluatedAfterOwnWrite = false;
            try
            {
                if (RunOwnerLoop(data, property, token, ref state))
                {
                    break;
                }
            }
            catch (Exception exception)
            {
                isFirstException = firstException is null;
                firstException ??= ExceptionDispatchInfo.Capture(exception);
            }

            if (!TryTakeOverAfterFailure(data, ref token, ref state, isOwnRequestTakenOver: isFirstException))
            {
                break;
            }
        }

        firstException?.Throw();
    }

    /// <summary>
    /// The request a loop took since its last commit and has not answered with a commit yet: none, only requests
    /// raised on the owner's thread, or a foreign one (another thread's request, or a write of another thread
    /// that moved a source without a request, R5).
    /// </summary>
    private enum UnansweredRequest
    {
        None,
        Own,
        Foreign
    }

    /// <summary>The state an owner loop carries from one iteration to the next.</summary>
    private struct OwnerLoopState
    {
        /// <summary>
        /// The raw changed timestamp of the request being evaluated; 0 for a request without one, which its
        /// publication resolves.
        /// </summary>
        public long RawTimestamp;

        public UnansweredRequest Unanswered;

        /// <summary>
        /// The attach count when the loop took its last request, read under lock(data) at the take: an attach that
        /// committed since evaluated after every request the loop took.
        /// </summary>
        public long AttachCount;

        /// <summary>The loop evaluated again since its last commit because the getter wrote a source it had read.</summary>
        public bool HasEvaluatedAfterOwnWrite;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static UnansweredRequest Add(UnansweredRequest unanswered, bool isForeign)
    {
        var request = isForeign ? UnansweredRequest.Foreign : UnansweredRequest.Own;
        return request > unanswered ? request : unanswered;
    }

    // Returns true when the loop released ownership, false when the iteration limit was reached while it owns the property.
    private static bool RunOwnerLoop(DerivedPropertyData data, PropertyReference property, long token, ref OwnerLoopState state)
    {
        for (var iteration = 0; iteration < MaxStabilizationIterations; iteration++)
        {
            if (RunOwnerIteration(data, property, token, ref state))
            {
                return true;
            }
        }

        Trace.TraceWarning(
            $"DerivedPropertyChangeHandler: MaxStabilizationIterations ({MaxStabilizationIterations}) exhausted for " +
            $"'{property.Metadata.Name}' on {property.Subject.GetType().Name}.");
        return false;
    }

    /// <summary>
    /// One evaluation of the owner loop: evaluates and commits, publishes outside the lock while still owning
    /// the property, then continues with a pending request or releases ownership. Returns true when it released.
    /// </summary>
    private static bool RunOwnerIteration(DerivedPropertyData data, PropertyReference property, long token, ref OwnerLoopState state)
    {
        var commit = EvaluateAndCommit(data, property, token, ref state);
        if (commit.Outcome != CommitOutcome.Committed)
        {
            return commit.Outcome != CommitOutcome.EvaluateAgain;
        }

        state.Unanswered = UnansweredRequest.None;
        state.HasEvaluatedAfterOwnWrite = false;

        if (commit.ShouldPublish)
        {
            NotifyDerivedPropertyChanged(property, data, commit.Sequence, commit.NewValue, commit.OldValue, state.RawTimestamp);
        }

        lock (data)
        {
            if (data.OwnerToken != token)
            {
                return true;
            }

            if (data.RecalculationNeeded && data.IsAttached && data.AttachToken == 0)
            {
                state.Unanswered = Add(UnansweredRequest.None, TakePendingRequest(data, out state.RawTimestamp));
                state.AttachCount = data.AttachCount;
                return false;
            }

            // Detached, or an attach is in progress, which takes over a pending request (RecalculationNeeded stays).
            data.OwnerToken = 0;
            return true;
        }
    }

    // Called after a failed owner loop: releases ownership and takes a pending request over with a new token. A
    // request raised on this thread alone is dropped unless isOwnRequestTakenOver.
    private static bool TryTakeOverAfterFailure(
        DerivedPropertyData data, ref long token, ref OwnerLoopState state, bool isOwnRequestTakenOver)
    {
        lock (data)
        {
            if (data.OwnerToken != token)
            {
                return false;
            }

            data.OwnerToken = 0;

            // Nothing is pending, the property is detached, or an attach is in progress, which evaluates again and
            // takes over a set flag.
            var hasNewRequest = data.RecalculationNeeded;
            if ((!hasNewRequest && state.Unanswered == UnansweredRequest.None) || !data.IsAttached || data.AttachToken != 0)
            {
                return false;
            }

            var isForeign = TakePendingRequest(data, out var pendingTimestamp) || state.Unanswered == UnansweredRequest.Foreign;
            if (!isForeign && !isOwnRequestTakenOver)
            {
                return false;
            }

            // A request carried over from the failed loop keeps the timestamp it adopted; a newer one wins.
            if (hasNewRequest)
            {
                state.RawTimestamp = pendingTimestamp;
            }

            token = TakeOwnership(data, DerivedPropertyData.NewToken());
            state.AttachCount = data.AttachCount;
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
    private static Commit EvaluateAndCommit(DerivedPropertyData data, PropertyReference property, long token, ref OwnerLoopState state)
    {
        AfterTakeForTests?.Invoke(property);
        var evaluation = Evaluate(data, property);
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
                state.Unanswered = Add(state.Unanswered, TakePendingRequest(data, out state.RawTimestamp));
                state.AttachCount = data.AttachCount;
                return new Commit(CommitOutcome.EvaluateAgain);
            }

            if (data.AttachCount != state.AttachCount)
            {
                // An attach has committed since the loop took its last request: the attach's value is newer, and the
                // attach requested a recalculation after it, which answers the requests this loop took.
                DiscardRecording();
                data.OwnerToken = 0;
                return new Commit(CommitOutcome.Released);
            }

            var check = CommitLinks(data, property);
            if (check == RecordingCheck.SourceMoved)
            {
                // A source moved without a request (its writer read the used-by set before this link). The
                // recording follows this thread's own writes, so the writer is another thread.
                state.Unanswered = UnansweredRequest.Foreign;
                return new Commit(CommitOutcome.EvaluateAgain);
            }

            if (!IsStable(check, ref state.HasEvaluatedAfterOwnWrite))
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

            return CommitValue(data, property, evaluation.Value, state.RawTimestamp);
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
        if (rawTimestamp != 0)
        {
            // A request without a timestamp is stamped by its publication.
            property.SetWriteTimestamp(rawTimestamp > 0 ? rawTimestamp : 0L);
        }

        return new Commit(CommitOutcome.Committed, ShouldPublish: shouldPublish, OldValue: oldValue, NewValue: newValue, Sequence: sequence);
    }

    // The boxed default of a nullable value type is null.
    private static object? GetDefault(Type type) =>
        type.IsValueType && Nullable.GetUnderlyingType(type) is null ? RuntimeHelpers.GetUninitializedObject(type) : null;

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
        var previousPublication = TrackingWriteObserver.EnterPublication(data);
        try
        {
            derivedProperty.PublishDerivedPropertyValue(newValue, oldValue, rawTimestamp, finalValueIsNewValue: true);
        }
        finally
        {
            TrackingWriteObserver.ExitPublication(previousPublication);
        }

        if (derivedProperty.Subject is IRaisePropertyChanged raiser)
        {
            raiser.RaisePropertyChanged(derivedProperty.Metadata.Name);
        }
    }

    private readonly record struct Evaluation(object? Value, bool HasThrown);

    /// <summary>
    /// Runs the getter outside every lock and records what it read; the recording stays on the
    /// recorder until <see cref="CommitLinks"/> or <see cref="DiscardRecording"/> clears it.
    /// </summary>
    private static Evaluation Evaluate(DerivedPropertyData data, in PropertyReference property)
    {
        var recorder = DerivedPropertyRecorder.Current;
        recorder.StartRecording(property, data.RequiredData);
        try
        {
            return new Evaluation(property.Metadata.GetValue?.Invoke(property.Subject), HasThrown: false);
        }
        catch (Exception)
        {
            // The reads before the throw are the ones that decided it; they are kept as dependencies.
            return new Evaluation(null, HasThrown: true);
        }
        finally
        {
            recorder.FinishRecording();
        }
    }

    /// <summary>
    /// Sets the links to the recorded reads and reports whether a source moved since it was read. Called under
    /// lock(data).
    /// </summary>
    private static RecordingCheck CommitLinks(DerivedPropertyData data, in PropertyReference property)
    {
        var recorder = DerivedPropertyRecorder.Current;
        try
        {
            var recorded = recorder.LastRecording;
            data.UpdateDependencies(property, recorded);
            if (HasVersionChanged(recorded))
            {
                return RecordingCheck.SourceMoved;
            }

            return recorder.LastRecordingHasOwnWriteAfterRead ? RecordingCheck.OwnWriteAfterRead : RecordingCheck.Current;
        }
        finally
        {
            recorder.ClearLastRecording();
        }
    }

    private static void DiscardRecording() => DerivedPropertyRecorder.Current.ClearLastRecording();

    private enum RecordingCheck
    {
        /// <summary>No source moved since the evaluation read it.</summary>
        Current,

        /// <summary>A write of another thread moved a source after the evaluation read it.</summary>
        SourceMoved,

        /// <summary>
        /// Only writes of the evaluating thread moved a source after the evaluation read it: the value may stem from
        /// before such a write, and a write of a source not linked yet requests nothing.
        /// </summary>
        OwnWriteAfterRead
    }

    // An own write after a read is answered by exactly one further evaluation per commit, so a getter that writes on
    // every evaluation (for example to a subject it creates) still commits.
    private static bool IsStable(RecordingCheck check, ref bool hasEvaluatedAfterOwnWrite)
    {
        if (check == RecordingCheck.OwnWriteAfterRead && !hasEvaluatedAfterOwnWrite)
        {
            hasEvaluatedAfterOwnWrite = true;
            return false;
        }

        return check != RecordingCheck.SourceMoved;
    }

    private static bool HasVersionChanged(ReadOnlySpan<RecordedDependency> recorded)
    {
        // R5: a full fence between setting the links and reading the versions, paired with the writer's Interlocked
        // increment of the version (W4) before it reads the used-by set (W5): either a version read here sees the
        // write, or the writer sees the link.
        Interlocked.MemoryBarrier();
        foreach (ref readonly var dependency in recorded)
        {
            if (Volatile.Read(ref dependency.Data.Version) != dependency.Version)
            {
                return true;
            }
        }

        return false;
    }
}
