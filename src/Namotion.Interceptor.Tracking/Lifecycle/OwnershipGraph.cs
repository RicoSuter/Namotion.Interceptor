using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// The committed ownership state of one context: which subjects it owns, the last occurrence snapshot
/// of every structural property, and the primitives that claim executors for this context or hand
/// them back.
/// </summary>
/// <remarks>
/// Property snapshots are the outgoing truth. Incoming edge identities contain the same property
/// and child-specific ordinal, while their index or key is publication payload only.
///
/// The claim primitives (claiming, releasing and re-anchoring executors) take the executor's
/// attachment monitor through <c>TryUpdateAttachment</c>, so they require the topology gate to
/// already be held; see the lock order note on the executor's attachment monitor.
///
/// The owned map is a <see cref="ConcurrentDictionary{TKey,TValue}"/> with exactly one writer (the
/// lifecycle, under its topology lock). It is concurrent for the readers: <c>GetParents</c> and
/// <c>GetReferenceCount</c> must not take that lock, so they need a lock-free way to find a
/// subject's record.
/// </remarks>
internal sealed class OwnershipGraph(
    IInterceptorSubjectContext context,
    Func<IDisposable> enterTopologyGate)
{
    // Reference equality, explicitly: graph membership is identity, and a hand-written subject
    // may override Equals/GetHashCode, which under default equality could merge distinct nodes or
    // strand a subject whose hash mutates while it is owned.
    private readonly ConcurrentDictionary<IInterceptorSubject, SubjectOwnership> _owned = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<PropertyReference, StructuralSnapshot> _snapshots = new(PropertyReference.Comparer);

    // Written only by the release descent, under the topology lock, and read only by the
    // admission path; a set rather than a field because a release can nest inside a callback.
    private readonly HashSet<IInterceptorSubject> _releasing = new(ReferenceEqualityComparer.Instance);

    public IInterceptorSubjectContext Context { get; } = context;

    /// <summary>
    /// Whether the property can carry graph edges: intercepted, so the lifecycle sees its writes,
    /// of a declared type that can contain subjects, and not a derived projection.
    /// </summary>
    /// <remarks>
    /// A [Derived] property carries an edge where it is the store of record. The generator gives
    /// every intercepted property a backing field, so there IsIntercepted already means the
    /// property is the store; a dynamic property is intercepted unconditionally, so its setter
    /// stands in instead, because a getter-only derived one can return nothing the properties it
    /// reads do not already own. A subject reachable only through a property that carries no edge
    /// is never tracked, and DerivedPropertyChangeHandler rejects it instead of letting it go
    /// silently unowned. See docs/design/tracking-lifecycle.md.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsStructural(in SubjectPropertyMetadata metadata)
    {
        return metadata is { IsIntercepted: true } and not { IsDerived: true, IsDynamic: true, SetValue: null } &&
               metadata.Type.CanContainSubjects();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SubjectOwnership? TryGetOwnership(IInterceptorSubject subject)
    {
        return _owned.TryGetValue(subject, out var ownership) ? ownership : null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsOwned(IInterceptorSubject subject)
    {
        return _owned.ContainsKey(subject);
    }

    public SubjectOwnership AddOwnership(IInterceptorSubject subject)
    {
        var ownership = new SubjectOwnership();
        _owned[subject] = ownership;
        return ownership;
    }

    public void RemoveOwnership(IInterceptorSubject subject)
    {
        _owned.TryRemove(subject, out _);
    }

    /// <summary>
    /// Whether the subject is between losing its ownership record and having its executor handed
    /// back, which is the window its detach callbacks run in.
    /// </summary>
    /// <remarks>
    /// In that window the subject is attached but unowned, which is also the shape of a subject an
    /// attach descent has claimed but not published yet. The two need opposite admission
    /// behaviour, so the release marks its own; nothing else can tell them apart.
    /// </remarks>
    public bool IsReleasing(IInterceptorSubject subject)
    {
        return _releasing.Count > 0 && _releasing.Contains(subject);
    }

    /// <inheritdoc cref="IsReleasing"/>
    public void MarkReleasing(IInterceptorSubject subject)
    {
        _releasing.Add(subject);
    }

    /// <inheritdoc cref="IsReleasing"/>
    public void ClearReleasing(IInterceptorSubject subject)
    {
        _releasing.Remove(subject);
    }

    /// <summary>
    /// Gets the subject's occurrence-aware parents. Publication is lazily activated: the first call
    /// on a subject materializes its snapshot and marks it, and from then on every edge change
    /// republishes it, so a consumer that never asks pays one volatile read per edge change and
    /// allocates nothing.
    /// </summary>
    /// <remarks>
    /// This must not take the lifecycle's topology lock. <c>SourceMonitor</c> holds its own lock
    /// across a graph walk that calls it, and is also invoked from inside the topology lock through
    /// <c>HandleLifecycleChange</c>; a locking read would make those two orders opposite and
    /// deadlock. The lifecycle stays the sole writer; the owned-subject map is concurrent so the
    /// record can be found without the lock, and the per-subject monitor that guards materialization
    /// is a leaf that the topology lock is always taken before.
    /// </remarks>
    public ImmutableArray<SubjectParent> GetParents(IInterceptorSubject subject)
    {
        var ownership = TryGetOwnership(subject);
        if (ownership is null)
        {
            return [];
        }

        return ownership.TryGetPublishedParents(out var published) ? published : ownership.ActivateParents();
    }

    #region Property snapshots, which are also the committed outgoing edges

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public StructuralSnapshot GetSnapshot(PropertyReference property)
    {
        return _snapshots.GetValueOrDefault(property, StructuralSnapshot.Empty);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetSnapshot(PropertyReference property, StructuralSnapshot snapshot)
    {
        _snapshots[property] = snapshot;
    }

    /// <summary>
    /// Whether a snapshot entry exists at all. An empty committed value and a missing entry both
    /// expose no occurrences, so released-subject tests use this distinction.
    /// </summary>
    public bool HasSnapshot(PropertyReference property)
    {
        return _snapshots.ContainsKey(property);
    }

    /// <summary>
    /// Whether the committed snapshot contains the exact child occurrence named by an incoming edge.
    /// </summary>
    public bool ContainsOccurrence(PropertyReference property, IInterceptorSubject target, int subjectOrdinal)
    {
        if (!_snapshots.TryGetValue(property, out var snapshot))
        {
            return false;
        }

        foreach (var occurrence in snapshot.Occurrences)
        {
            if (occurrence.SubjectOrdinal == subjectOrdinal && ReferenceEquals(occurrence.Subject, target))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Walks the subject's structural properties and appends the occurrences their values contain, in
    /// property enumeration order and then value order, which is the order the release descent visits
    /// children in. Seeding reads the current getter output and commits its snapshot; collecting
    /// reads the committed snapshot. That one difference is why both callers exist.
    /// </summary>
    public void CollectStructuralChildren(
        IInterceptorSubject subject,
        List<(PropertyReference Property, StructuralOccurrence Occurrence)> children,
        bool seed)
    {
        foreach (var entry in subject.Properties)
        {
            var metadata = entry.Value;
            if (!IsStructural(metadata))
            {
                continue;
            }

            var property = new PropertyReference(subject, entry.Key);
            StructuralSnapshot snapshot;
            if (seed)
            {
                snapshot = StructuralSnapshotBuilder.Build(metadata.Type, metadata.GetValue?.Invoke(subject), 0);
                _snapshots[property] = snapshot;
            }
            else if (!_snapshots.TryGetValue(property, out snapshot!))
            {
                continue;
            }

            foreach (var occurrence in snapshot.Occurrences)
            {
                children.Add((property, occurrence));
            }
        }
    }

    /// <summary>
    /// Whether the subject's structural properties already carry committed snapshots, which is what
    /// tells an attach whether the subject's own component still has to be discovered. Checking the
    /// snapshots rather than a flag keeps one source of truth: seeding is exactly what writes them.
    /// </summary>
    /// <remarks>
    /// The first structural property answers for all of them: seeding writes every snapshot of a
    /// subject under the topology lock, so they are present or absent together.
    /// </remarks>
    public bool AreSnapshotsSeeded(IInterceptorSubject subject)
    {
        foreach (var entry in subject.Properties)
        {
            if (IsStructural(entry.Value))
            {
                return _snapshots.ContainsKey(new PropertyReference(subject, entry.Key));
            }
        }

        return true;
    }

    /// <summary>Drops every structural snapshot of the subject; called when it leaves the graph.</summary>
    public void RemoveSnapshots(IInterceptorSubject subject)
    {
        foreach (var entry in subject.Properties)
        {
            if (IsStructural(entry.Value))
            {
                _snapshots.Remove(new PropertyReference(subject, entry.Key));
            }
        }
    }

    #endregion

    #region Anchors and executor claims

    internal IDisposable ReserveForStructuralWrite(IInterceptorSubject subject)
    {
        return ((InterceptorExecutor)subject.Executor).TryAcquireOwnershipReservation(
            (InterceptorSubjectContext)Context,
            ReservationMode.Shared);
    }

    internal bool HasReservation(IInterceptorSubject subject)
    {
        return ((InterceptorExecutor)subject.Executor).HasOwnershipReservation(
            (InterceptorSubjectContext)Context);
    }

    internal void ReleaseUnusedReservation(IDisposable participant)
    {
        var reservation = (OwnershipReservationToken)participant;
        using (enterTopologyGate())
        {
            var subject = reservation.Subject;
            subject.Executor.TryGetAttachment(out var attachedContext, out var anchor, out _);
            var hasCommittedSupport = IsOwned(subject) ||
                                      (anchor != SubjectAttachmentAnchorKind.None &&
                                       ReferenceEquals(attachedContext, Context));
            reservation.ReleaseUnused(
                !hasCommittedSupport && ReferenceEquals(attachedContext, Context));
        }
    }

    /// <summary>
    /// Whether the subject carries a root anchor on this context. The anchor lives on the executor
    /// and is never mirrored into the graph state, so there is nothing to keep in sync.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsAnchored(IInterceptorSubject subject)
    {
        // One snapshot rather than the two getters: the anchor only means anything against the
        // context it anchors to, and the two getters can straddle a transition.
        subject.Executor.TryGetAttachment(out var attachedContext, out var anchor, out _);
        return anchor != SubjectAttachmentAnchorKind.None && ReferenceEquals(attachedContext, Context);
    }

    /// <summary>
    /// Legacy Task 8/9 adapter. It uses the executor reservation as its only claim state, commits
    /// immediately for the pre-transaction attach/admission paths, then releases its participant.
    /// </summary>
    public bool TryClaim(IInterceptorSubject subject, SubjectAttachmentAnchorKind anchor)
    {
        try
        {
            using var reservation = ((InterceptorExecutor)subject.Executor).TryAcquireOwnershipReservation(
                (InterceptorSubjectContext)Context,
                anchor == SubjectAttachmentAnchorKind.None ? ReservationMode.Shared : ReservationMode.Exclusive);
            CommitReservation(reservation, anchor);
            return true;
        }
        catch (LifecycleConflictException)
        {
            return false;
        }
    }

    /// <summary>Hands the subject's executor back, which is what makes it unattached again.</summary>
    public void ReleaseClaim(IInterceptorSubject subject)
    {
        var executor = subject.Executor;
        while (true)
        {
            executor.TryGetAttachment(out var attachedContext, out _, out var revision);
            if (!ReferenceEquals(attachedContext, Context))
            {
                return;
            }

            if (executor.TryUpdateAttachment(revision, null, SubjectAttachmentAnchorKind.None, out _))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Sets the subject's anchor: promoted by an explicit attach on an inherited subject, and
    /// cleared to <see cref="SubjectAttachmentAnchorKind.None"/> by an explicit detach. A non-null
    /// <paramref name="onlyFrom"/> writes the anchor only where the subject currently carries
    /// exactly that one; anchor adoption passes
    /// <see cref="SubjectAttachmentAnchorKind.Provisional"/>, because an explicit anchor that landed
    /// concurrently must survive rather than be degraded by the adoption.
    /// </summary>
    public void SetAnchor(
        IInterceptorSubject subject,
        SubjectAttachmentAnchorKind anchor,
        SubjectAttachmentAnchorKind? onlyFrom = null,
        OwnershipReservationToken? reservation = null)
    {
        if (reservation is not null && !ReferenceEquals(reservation.Subject, subject))
        {
            throw new InvalidOperationException("The ownership reservation belongs to a different subject.");
        }

        var executor = subject.Executor;
        while (true)
        {
            executor.TryGetAttachment(out var attachedContext, out var currentAnchor, out var revision);
            if (!ReferenceEquals(attachedContext, Context) ||
                (onlyFrom is null ? currentAnchor == anchor : currentAnchor != onlyFrom))
            {
                return;
            }

            var updated = reservation is not null
                ? reservation.TryUpdateAttachment(
                    revision,
                    (InterceptorSubjectContext)Context,
                    anchor,
                    out _)
                : executor.TryUpdateAttachment(revision, Context, anchor, out _);
            if (updated)
            {
                return;
            }
        }
    }

    #endregion

    #region Component discovery

    /// <summary>
    /// Walks the structural component the value opens up, validating every subject against this
    /// context and collecting the unattached ones so they can be claimed as one batch before
    /// anything is published.
    /// </summary>
    /// <remarks>
    /// The walk descends into unattached subjects only. An attached same-context subject was
    /// validated when it was attached and its own component is already owned, so there is nothing
    /// below it that could be foreign. That bound is what keeps the cost proportional to the newly
    /// arriving subgraph rather than to the graph.
    /// </remarks>
    public void DiscoverComponent(
        Type declaredType,
        object? value,
        HashSet<IInterceptorSubject> visited,
        List<IInterceptorSubject> discovered,
        bool includeAttached = false)
    {
        var snapshot = StructuralSnapshotBuilder.Build(declaredType, value, 0);
        foreach (var occurrence in snapshot.Occurrences)
        {
            DiscoverComponent(occurrence.Subject, visited, discovered, includeAttached);
        }
    }

    /// <inheritdoc cref="DiscoverComponent(Type,object?,HashSet{IInterceptorSubject},List{IInterceptorSubject},bool)"/>
    public void DiscoverComponent(
        IInterceptorSubject start,
        HashSet<IInterceptorSubject> visited,
        List<IInterceptorSubject> discovered,
        bool includeAttached = false)
    {
        var pending = LifecycleScratch.RentSubjectStack();
        try
        {
            pending.Push(start);
            while (pending.Count > 0)
            {
                var subject = pending.Pop();
                if (!visited.Add(subject))
                {
                    continue;
                }

                var attachedContext = subject.Executor.AttachedContext;
                if (attachedContext is not null)
                {
                    if (!ReferenceEquals(attachedContext, Context))
                    {
                        throw new InvalidOperationException(
                            $"The subject '{subject.GetType().Name}' is owned by a different context and cannot " +
                            "join this graph. Detach it from that context first.");
                    }

                    if (includeAttached)
                    {
                        discovered.Add(subject);
                    }

                    continue;
                }

                discovered.Add(subject);

                foreach (var entry in subject.Properties)
                {
                    if (!IsStructural(entry.Value))
                    {
                        continue;
                    }

                    var childValue = entry.Value.GetValue?.Invoke(subject);
                    if (childValue is null)
                    {
                        continue;
                    }

                    var snapshot = StructuralSnapshotBuilder.Build(entry.Value.Type, childValue, 0);
                    foreach (var occurrence in snapshot.Occurrences)
                    {
                        pending.Push(occurrence.Subject);
                    }
                }
            }
        }
        finally
        {
            LifecycleScratch.Return(pending);
        }
    }

    /// <summary>
    /// Reserves every discovered subject. A lost race releases this operation's participants and
    /// reports failure, so the caller can throw before touching the backing property.
    /// </summary>
    public bool TryReserveDiscovered(
        List<IInterceptorSubject> unattached,
        Dictionary<IInterceptorSubject, OwnershipReservationToken> reservations)
    {
        for (var i = 0; i < unattached.Count; i++)
        {
            var subject = unattached[i];
            if (reservations.ContainsKey(subject))
            {
                continue;
            }

            try
            {
                var reservation = (OwnershipReservationToken)ReserveForStructuralWrite(subject);
                reservations.Add(subject, reservation);
            }
            catch (LifecycleConflictException)
            {
                ReleaseUnusedReservations(reservations);
                return false;
            }
        }

        return true;
    }

    public bool TryClaimDiscovered(List<IInterceptorSubject> unattached, IInterceptorSubject? explicitRoot, SubjectAttachmentAnchorKind rootAnchor)
    {
        for (var i = 0; i < unattached.Count; i++)
        {
            var subject = unattached[i];
            var anchor = ReferenceEquals(subject, explicitRoot) ? rootAnchor : SubjectAttachmentAnchorKind.None;
            if (TryClaim(subject, anchor))
            {
                continue;
            }

            for (var j = 0; j < i; j++)
            {
                ReleaseClaim(unattached[j]);
            }

            return false;
        }

        return true;
    }

    public void CommitReservations(Dictionary<IInterceptorSubject, OwnershipReservationToken> reservations)
    {
        foreach (var reservation in reservations.Values)
        {
            CommitReservation(reservation, SubjectAttachmentAnchorKind.None);
        }
    }

    public void ReleaseUnusedReservations(Dictionary<IInterceptorSubject, OwnershipReservationToken> reservations)
    {
        foreach (var reservation in reservations.Values)
        {
            ReleaseUnusedReservation(reservation);
        }

        reservations.Clear();
    }

    public void ReleaseUnusedClaims(List<IInterceptorSubject> claimed)
    {
        foreach (var subject in claimed)
        {
            if (!IsOwned(subject) && !IsAnchored(subject))
            {
                ReleaseClaim(subject);
            }
        }
    }

    private void CommitReservation(
        OwnershipReservationToken reservation,
        SubjectAttachmentAnchorKind anchor)
    {
        var executor = reservation.Subject.Executor;
        while (true)
        {
            executor.TryGetAttachment(out var attachedContext, out var currentAnchor, out var revision);
            if (attachedContext is not null && !ReferenceEquals(attachedContext, Context))
            {
                throw LifecycleConflictException.Retryable(reservation.Subject);
            }

            if (attachedContext is not null &&
                (anchor == SubjectAttachmentAnchorKind.None || currentAnchor == anchor ||
                 currentAnchor == SubjectAttachmentAnchorKind.Explicit))
            {
                return;
            }

            if (reservation.TryUpdateAttachment(
                revision,
                (InterceptorSubjectContext)Context,
                anchor,
                out _))
            {
                return;
            }
        }
    }

    #endregion
}
