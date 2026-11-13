using System.Diagnostics;
using System.Runtime.CompilerServices;
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
                // A recalculation from before a detach is in flight: it hands off at its next lock.
                data.RecalculationNeeded = true;
            }
        }

        EvaluateAtAttach(data, property, attachToken, ownsData);
    }

    /// <inheritdoc />
    /// <remarks>
    /// A detach issued by a lifecycle interceptor is ignored: the subject may still be attached to
    /// another graph, and the lifecycle detaches the derived properties itself once the subject left
    /// its last graph (<see cref="DetachDerivedProperties"/>). A direct call always detaches, except one
    /// made by a lifecycle handler while the lifecycle detaches that subject's properties.
    /// </remarks>
    public void DetachProperty(SubjectPropertyLifecycleChange change)
    {
        if (!SubjectLifecycleAttachments.IsLifecycleDetach)
        {
            DetachDerivedProperty(change.Property);
        }
    }

    /// <summary>Detaches every derived property of a subject that left its last lifecycle interceptor.</summary>
    internal static void DetachDerivedProperties(IInterceptorSubject subject)
    {
        foreach (var name in subject.Properties.Keys)
        {
            DetachDerivedProperty(new PropertyReference(subject, name), onlyIfSubjectIsDetached: true);
        }
    }

    private static void DetachDerivedProperty(in PropertyReference property, bool onlyIfSubjectIsDetached = false)
    {
        if (!property.Metadata.IsDerived)
        {
            return;
        }

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

        RunOwnerLoop(data, property, token, rawTimestamp);
    }

    /// <summary>
    /// Evaluates the getter at attach, outside lock(data), and commits the first value without publishing it.
    /// The attach token decides whether the commit counts: a newer attach or a detach supersedes it.
    /// </summary>
    private static void EvaluateAtAttach(DerivedPropertyData data, PropertyReference property, long attachToken, bool ownsData)
    {
        var requestAfterAttach = false;
        long pendingTimestamp = 0;

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

                var isStable = IsAttachEvaluationStable(data, property, ownsData);
                if (!isStable && !isLastIteration)
                {
                    continue;
                }

                CommitAttachEvaluation(data, property, evaluation, isStable, attachToken, ownsData);
                requestAfterAttach = TakePendingRequestIfUnowned(data, out pendingTimestamp);
                break;
            }
        }

        if (requestAfterAttach)
        {
            RequestRecalculation(property, pendingTimestamp, isHandOver: true);
        }
    }

    // Called under lock(data). When the attach owns the property, a request that arrived during the
    // evaluation is answered by evaluating again; otherwise the flag belongs to the owner in flight.
    private static bool IsAttachEvaluationStable(DerivedPropertyData data, in PropertyReference property, bool ownsData)
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

    private static void RunOwnerLoop(DerivedPropertyData data, PropertyReference property, long token, long rawTimestamp)
    {
        var isOwner = true;
        try
        {
            for (var iteration = 0; iteration < MaxStabilizationIterations; iteration++)
            {
                var step = RunOwnerIteration(data, property, token, ref rawTimestamp);
                if (step == OwnerStep.Continue)
                {
                    continue;
                }

                isOwner = false;
                return;
            }

            Trace.TraceWarning(
                $"DerivedPropertyChangeHandler: MaxStabilizationIterations ({MaxStabilizationIterations}) exhausted for " +
                $"'{property.Metadata.Name}' on {property.Subject.GetType().Name}.");
        }
        finally
        {
            if (isOwner)
            {
                ReleaseAfterFailure(data, property, token);
            }
        }
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
        if (!TryEvaluateAndCommit(data, property, token, ref rawTimestamp, out var commit))
        {
            if (!commit.IsReleased)
            {
                return OwnerStep.Continue;
            }

            if (commit.HasPendingRequest)
            {
                RequestRecalculation(property, commit.PendingTimestamp, isHandOver: true);
            }

            return OwnerStep.Released;
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

    // An observer threw while this loop owned the property, or the iteration limit was reached:
    // release ownership and hand a pending request to a new owner instead of losing it.
    private static void ReleaseAfterFailure(DerivedPropertyData data, PropertyReference property, long token)
    {
        long pendingTimestamp;
        lock (data)
        {
            if (data.OwnerToken != token)
            {
                return;
            }

            data.OwnerToken = 0;
            if (!data.RecalculationNeeded || !data.IsAttached || data.AttachToken != 0)
            {
                return;
            }

            data.RecalculationNeeded = false;
            pendingTimestamp = TakePendingTimestamp(data);
        }

        // A new owner for the pending request; an owner loop that keeps failing or exhausting hands over again.
        RequestRecalculation(property, pendingTimestamp, isHandOver: true);
    }

    private readonly record struct Commit(
        bool IsReleased, bool HasPendingRequest, long PendingTimestamp, bool ShouldPublish, object? OldValue, object? NewValue, long Sequence);

    /// <summary>
    /// Evaluates outside lock(data) and commits under it. Returns true when a value was committed; false with
    /// <see cref="Commit.IsReleased"/> when this loop no longer owns the property, false otherwise when it has
    /// to evaluate again.
    /// </summary>
    private static bool TryEvaluateAndCommit(
        DerivedPropertyData data, PropertyReference property, long token, ref long rawTimestamp, out Commit commit)
    {
        var evaluation = Evaluate(property);
        lock (data)
        {
            commit = default;
            if (data.OwnerToken != token)
            {
                DiscardRecording();
                commit = commit with { IsReleased = true };
                return false;
            }

            if (!data.IsAttached || data.AttachToken != 0)
            {
                // Detached, or an attach is in progress and takes over (RecalculationNeeded stays).
                DiscardRecording();
                data.OwnerToken = 0;
                commit = commit with { IsReleased = true };
                return false;
            }

            if (data.RecalculationNeeded)
            {
                DiscardRecording();
                data.RecalculationNeeded = false;
                rawTimestamp = AdoptPendingTimestamp(data);
                return false;
            }

            if (!CommitLinks(data, property))
            {
                return false;
            }

            if (evaluation.HasThrown)
            {
                // The previous value stays; the first success afterwards is published (IsFaulted).
                data.IsFaulted = true;
                data.OwnerToken = 0;
                var hasPendingRequest = TakePendingRequestIfUnowned(data, out var pendingTimestamp);
                commit = commit with { IsReleased = true, HasPendingRequest = hasPendingRequest, PendingTimestamp = pendingTimestamp };
                return false;
            }

            commit = CommitValue(data, property, evaluation.Value, rawTimestamp);
            return true;
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
        return new Commit(false, false, 0, shouldPublish, oldValue, newValue, sequence);
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
    /// recorder until <see cref="CommitLinks"/> or <see cref="DiscardRecording"/>.
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
        var recorded = recorder.LastRecording;
        data.UpdateDependencies(property, recorded);
        var hasVersionChanged = HasVersionChanged(recorded);
        recorder.ClearLastRecording();
        return !hasVersionChanged;
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
