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
internal sealed class OwnershipGraph(
    IInterceptorSubjectContext context,
    ITopologyAdmissionCoordinator coordinator)
{
    private readonly ConcurrentDictionary<IInterceptorSubject, SubjectOwnership> _owned = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<PropertyReference, StructuralSnapshot> _snapshots = new(PropertyReference.Comparer);

    private readonly HashSet<IInterceptorSubject> _releasing = new(ReferenceEqualityComparer.Instance);

    public IInterceptorSubjectContext Context { get; } = context;

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

    public bool IsReleasing(IInterceptorSubject subject)
    {
        return _releasing.Count > 0 && _releasing.Contains(subject);
    }

    public void MarkReleasing(IInterceptorSubject subject)
    {
        _releasing.Add(subject);
    }

    public void ClearReleasing(IInterceptorSubject subject)
    {
        _releasing.Remove(subject);
    }

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

    internal sealed class PreparedTopologyChange(
        PropertyReference property,
        object? value,
        StructuralSnapshot oldSnapshot,
        StructuralSnapshot newSnapshot,
        Dictionary<PropertyReference, StructuralSnapshot>? seededSnapshots)
    {
        internal PropertyReference Property { get; } = property;
        internal object? Value { get; } = value;
        internal StructuralSnapshot OldSnapshot { get; } = oldSnapshot;
        internal StructuralSnapshot NewSnapshot { get; } = newSnapshot;
        internal Dictionary<PropertyReference, StructuralSnapshot>? SeededSnapshots { get; } = seededSnapshots;
    }

    internal PreparedTopologyChange PrepareWrite(
        PropertyReference property,
        object? value,
        StructuralSnapshot capturedSnapshot,
        long revision,
        Dictionary<PropertyReference, StructuralSnapshot>? seededSnapshots = null) =>
        new(property, value, GetSnapshot(property), capturedSnapshot with { SourceRevision = revision }, seededSnapshots);

    internal bool Publish(PreparedTopologyChange change)
    {
        if (!IsOwned(change.Property.Subject) || !ReferenceEquals(GetSnapshot(change.Property), change.OldSnapshot))
        {
            return false;
        }

        if (change.SeededSnapshots is not null)
        {
            foreach (var entry in change.SeededSnapshots)
            {
                SetSnapshot(entry.Key, entry.Value);
            }
        }

        SetSnapshot(change.Property, change.NewSnapshot);
        return true;
    }

    internal List<IInterceptorSubject>? PrepareDeferredSweep()
    {
        List<IInterceptorSubject>? result = null;
        foreach (var entry in _owned)
        {
            if (entry.Value.IncomingCount == 0 && !IsAnchored(entry.Key) &&
                !HasReservation(entry.Key) && !HasStructuralLease(entry.Key))
            {
                (result ??= []).Add(entry.Key);
            }
        }

        return result;
    }

    public bool HasSnapshot(PropertyReference property)
    {
        return _snapshots.ContainsKey(property);
    }

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
        return coordinator.AcquireOwnershipReservation((InterceptorExecutor)subject.Executor, ReservationMode.Shared);
    }

    internal bool HasReservation(IInterceptorSubject subject)
    {
        return ((InterceptorExecutor)subject.Executor).HasOwnershipReservation(
            (InterceptorSubjectContext)Context);
    }

    internal static bool HasStructuralLease(IInterceptorSubject subject) =>
        ((InterceptorExecutor)subject.Executor).StructuralLeaseCount > 0;

    internal void ReleaseUnusedReservation(IDisposable participant)
    {
        var reservation = (OwnershipReservationToken)participant;
        var subject = reservation.Subject;
        subject.Executor.TryGetAttachment(out var attachedContext, out var anchor, out _);
        reservation.Complete(IsOwned(subject) ||
            (anchor != SubjectAttachmentAnchorKind.None && ReferenceEquals(attachedContext, Context)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsAnchored(IInterceptorSubject subject)
    {
        subject.Executor.TryGetAttachment(out var attachedContext, out var anchor, out _);
        return anchor != SubjectAttachmentAnchorKind.None && ReferenceEquals(attachedContext, Context);
    }

    public bool TryClaim(IInterceptorSubject subject, SubjectAttachmentAnchorKind anchor)
    {
        try
        {
            using var reservation = coordinator.AcquireOwnershipReservation(
                (InterceptorExecutor)subject.Executor,
                anchor == SubjectAttachmentAnchorKind.None ? ReservationMode.Shared : ReservationMode.Exclusive);
            CommitReservation(reservation, anchor);
            return true;
        }
        catch (LifecycleConflictException)
        {
            return false;
        }
    }

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

    public void DiscoverComponent(
        Type declaredType,
        object? value,
        HashSet<IInterceptorSubject> visited,
        List<IInterceptorSubject> discovered,
        bool includeAttached = false)
    {
        var snapshot = StructuralSnapshotBuilder.Build(declaredType, value, 0);
        DiscoverComponent(snapshot, visited, discovered, includeAttached);
    }

    public void DiscoverComponent(
        StructuralSnapshot snapshot,
        HashSet<IInterceptorSubject> visited,
        List<IInterceptorSubject> discovered,
        bool includeAttached = false,
        Dictionary<PropertyReference, StructuralSnapshot>? seededSnapshots = null)
    {
        foreach (var occurrence in snapshot.Occurrences)
        {
            DiscoverComponent(occurrence.Subject, visited, discovered, includeAttached, seededSnapshots);
        }
    }

    public void DiscoverComponent(
        IInterceptorSubject start,
        HashSet<IInterceptorSubject> visited,
        List<IInterceptorSubject> discovered,
        bool includeAttached = false,
        Dictionary<PropertyReference, StructuralSnapshot>? seededSnapshots = null)
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

                    if (IsOwned(subject))
                    {
                        continue;
                    }
                }

                discovered.Add(subject);

                foreach (var entry in subject.Properties)
                {
                    if (!IsStructural(entry.Value))
                    {
                        continue;
                    }

                    var childValue = entry.Value.GetValue?.Invoke(subject);
                    var snapshot = StructuralSnapshotBuilder.Build(entry.Value.Type, childValue, 0);
                    seededSnapshots?.Add(new PropertyReference(subject, entry.Key), snapshot);
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

    internal void CommitReservation(
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
