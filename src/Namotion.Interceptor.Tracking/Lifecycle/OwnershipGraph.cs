using System.Collections;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Tracking.Lifecycle;

internal sealed class OwnershipGraph
{
    internal sealed record GraphState(ImmutableDictionary<IInterceptorSubject, SubjectOwnership> Owned,
        ImmutableDictionary<PropertyReference, StructuralSnapshot> Snapshots)
    {
        internal static readonly GraphState Empty = new(
            ImmutableDictionary.Create<IInterceptorSubject, SubjectOwnership>(ReferenceEqualityComparer.Instance),
            ImmutableDictionary.Create<PropertyReference, StructuralSnapshot>(PropertyReference.Comparer));
    }

    private readonly ITopologyAdmissionCoordinator _coordinator;
    private readonly bool _isPreparing;
    private volatile GraphState _state;
    private Dictionary<IInterceptorSubject, PreparedAttachmentTarget>? _preparedAttachments;
    private Dictionary<IInterceptorSubject, ImmutableArray<string>>? _preparedPropertyNames;
    private readonly HashSet<IInterceptorSubject> _releasing = new(ReferenceEqualityComparer.Instance);
    public IInterceptorSubjectContext Context { get; }
    internal OwnershipGraph(IInterceptorSubjectContext context, ITopologyAdmissionCoordinator coordinator)
    {
        Context = context;
        _coordinator = coordinator;
        _state = GraphState.Empty;
    }
    private OwnershipGraph(OwnershipGraph source)
    {
        Context = source.Context;
        _coordinator = source._coordinator;
        _isPreparing = true;
        var owned = source._state.Owned.ToBuilder();
        foreach (var subject in owned.Keys.ToArray())
        {
            owned[subject] = owned[subject].Clone();
        }

        _state = new GraphState(owned.ToImmutable(), source._state.Snapshots);
        _releasing.UnionWith(source._releasing);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsStructural(in SubjectPropertyMetadata metadata)
    {
        return metadata is { IsIntercepted: true } and not { IsDerived: true, IsDynamic: true, SetValue: null } &&
               metadata.Type.CanContainSubjects();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SubjectOwnership? TryGetOwnership(IInterceptorSubject subject)
    {
        return _state.Owned.TryGetValue(subject, out var ownership) ? ownership : null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsOwned(IInterceptorSubject subject)
    {
        return _state.Owned.ContainsKey(subject);
    }

    public SubjectOwnership AddOwnership(
        IInterceptorSubject subject,
        ImmutableArray<string>? propertyNames = null)
    {
        var ownership = new SubjectOwnership(propertyNames ?? subject.Properties.Keys.ToImmutableArray());
        var state = _state;
        _state = new GraphState(state.Owned.SetItem(subject, ownership), state.Snapshots);
        return ownership;
    }

    public void RemoveOwnership(IInterceptorSubject subject)
    {
        var state = _state;
        _state = new GraphState(state.Owned.Remove(subject), state.Snapshots);
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
        return TryGetOwnership(subject)?.Parents ?? [];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public StructuralSnapshot GetSnapshot(PropertyReference property)
    {
        return _state.Snapshots.GetValueOrDefault(property, StructuralSnapshot.Empty);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetSnapshot(PropertyReference property, StructuralSnapshot snapshot)
    {
        var state = _state;
        _state = new GraphState(state.Owned, state.Snapshots.SetItem(property, snapshot));
    }

    internal sealed class PreparedTopologyChange(
        OwnershipGraph owner,
        GraphState publication,
        ImmutableArray<InterceptorExecutor.AttachmentTransition> attachmentUpdates) : IDisposable
    {
        private bool _isPublished;

        internal void Publish()
        {
            owner._state = publication;
            foreach (var update in attachmentUpdates)
            {
                update.PublishPrepared();
            }

            _isPublished = true;
        }

        public void Dispose()
        {
            if (!_isPublished)
            {
                foreach (var update in attachmentUpdates)
                {
                    update.Dispose();
                }
            }
        }
    }

    internal PreparedTopologyChange PrepareWrite(
        PropertyReference property,
        object? value,
        StructuralSnapshot capturedSnapshot,
        long revision,
        Dictionary<PropertyReference, StructuralSnapshot> seededSnapshots,
        Dictionary<IInterceptorSubject, ImmutableArray<string>> seededPropertyNames,
        Dictionary<IInterceptorSubject, OwnershipReservationToken> reservations,
        LifecycleNotifier notifier)
    {
        var baseline = _state;
        if (!baseline.Owned.ContainsKey(property.Subject))
        {
            throw LifecycleConflictException.Retryable(property.Subject);
        }

        var prepared = new OwnershipGraph(this);
        prepared._preparedPropertyNames = seededPropertyNames;
        foreach (var entry in seededSnapshots)
        {
            prepared.SetSnapshot(entry.Key, entry.Value);
        }

        var oldSnapshot = baseline.Snapshots.GetValueOrDefault(property, StructuralSnapshot.Empty);
        var newSnapshot = capturedSnapshot with { SourceRevision = revision };
        prepared.SetSnapshot(property, newSnapshot);
        prepared.ReconcilePrepared(
            property,
            value,
            oldSnapshot.Occurrences,
            newSnapshot.Occurrences,
            reservations,
            notifier);

        return new PreparedTopologyChange(
            this,
            prepared._state,
            prepared.PrepareAttachmentUpdates(reservations));
    }

    internal void Publish(PreparedTopologyChange change) => change.Publish();

    internal void Reconcile(
        PropertyReference property,
        object? value,
        StructuralSnapshot snapshot,
        Dictionary<IInterceptorSubject, OwnershipReservationToken>? reservations,
        LifecycleNotifier notifier)
    {
        var previous = GetSnapshot(property);
        SetSnapshot(property, snapshot);
        ReconcilePrepared(
            property,
            value,
            previous.Occurrences,
            snapshot.Occurrences,
            reservations ?? [],
            notifier);
    }

    internal void SeedAndAttachChildren(
        IInterceptorSubject subject,
        Dictionary<IInterceptorSubject, OwnershipReservationToken>? reservations,
        bool seed,
        LifecycleNotifier notifier)
    {
        var children = LifecycleScratch.RentChildList();
        try
        {
            CollectStructuralChildren(subject, children, seed);
            foreach (var (property, occurrence) in children)
            {
                AttachPreparedEdge(
                    occurrence.Subject,
                    property,
                    occurrence.SubjectOrdinal,
                    occurrence.Index,
                    reservations ?? [],
                    notifier);
            }
        }
        finally
        {
            LifecycleScratch.Return(children);
        }
    }

    internal void AttachEdge(
        IInterceptorSubject subject,
        PropertyReference property,
        int subjectOrdinal,
        object? index,
        Dictionary<IInterceptorSubject, OwnershipReservationToken>? reservations,
        LifecycleNotifier notifier) =>
        AttachPreparedEdge(subject, property, subjectOrdinal, index, reservations ?? [], notifier);

    internal void AttachRoot(IInterceptorSubject subject, LifecycleNotifier notifier)
    {
        var ownership = AddOwnership(subject);
        var change = new SubjectLifecycleChange
        {
            Subject = subject,
            ReferenceCount = 0,
            IsContextAttach = true
        };
        var properties = ownership.PropertyNames;
        notifier.InvokeAddedLifecycleHandlers(subject, change);
        notifier.RaiseSubjectAttached(change);
        notifier.AttachSubjectProperties(subject, properties);
    }

    internal void RemoveEdge(
        IInterceptorSubject subject,
        PropertyReference property,
        int subjectOrdinal,
        object? index,
        LifecycleNotifier notifier) =>
        RemovePreparedEdge(subject, property, subjectOrdinal, index, notifier);

    internal void ReleaseRoot(IInterceptorSubject subject, LifecycleNotifier notifier)
    {
        if (TryGetOwnership(subject) is { } ownership)
        {
            ReleasePrepared(subject, ownership, null, null, notifier);
        }
    }

    private ImmutableArray<InterceptorExecutor.AttachmentTransition> PrepareAttachmentUpdates(
        Dictionary<IInterceptorSubject, OwnershipReservationToken> reservations)
    {
        if (_preparedAttachments is null)
        {
            return [];
        }

        var updates = ImmutableArray.CreateBuilder<InterceptorExecutor.AttachmentTransition>();
        try
        {
            foreach (var entry in _preparedAttachments)
            {
                var subject = entry.Key;
                var target = entry.Value;
                subject.Executor.TryGetAttachment(out var currentContext, out var currentAnchor, out _);
                if (ReferenceEquals(currentContext, target.Context) && currentAnchor == target.Anchor)
                {
                    continue;
                }

                reservations.TryGetValue(subject, out var reservation);
                updates.Add(((InterceptorExecutor)subject.Executor).PrepareAttachmentUpdate(
                    (InterceptorSubjectContext)Context,
                    target.Context,
                    target.Anchor,
                    target.Reservation ?? reservation));
            }

            return updates.ToImmutable();
        }
        catch
        {
            foreach (var update in updates)
            {
                update.Dispose();
            }

            throw;
        }
    }

    private void ReconcilePrepared(
        PropertyReference property,
        object? value,
        ImmutableArray<StructuralOccurrence> oldOccurrences,
        ImmutableArray<StructuralOccurrence> newOccurrences,
        Dictionary<IInterceptorSubject, OwnershipReservationToken> reservations,
        LifecycleNotifier notifier)
    {
        var oldCounts = LifecycleScratch.RentSubjectCounter();
        var newCounts = LifecycleScratch.RentSubjectCounter();
        try
        {
            foreach (var occurrence in oldOccurrences)
            {
                oldCounts[occurrence.Subject] = oldCounts.GetValueOrDefault(occurrence.Subject) + 1;
            }

            foreach (var occurrence in newOccurrences)
            {
                newCounts[occurrence.Subject] = newCounts.GetValueOrDefault(occurrence.Subject) + 1;
            }

            for (var index = oldOccurrences.Length - 1; index >= 0; index--)
            {
                var occurrence = oldOccurrences[index];
                var remaining = oldCounts[occurrence.Subject];
                if (remaining <= newCounts.GetValueOrDefault(occurrence.Subject))
                {
                    continue;
                }

                oldCounts[occurrence.Subject] = remaining - 1;
                RemovePreparedEdge(
                    occurrence.Subject,
                    property,
                    occurrence.SubjectOrdinal,
                    occurrence.Index,
                    notifier);
                if (!IsOwned(property.Subject))
                {
                    return;
                }
            }

            foreach (var occurrence in newOccurrences)
            {
                var retained = oldCounts.GetValueOrDefault(occurrence.Subject);
                if (retained > 0)
                {
                    oldCounts[occurrence.Subject] = retained - 1;
                    continue;
                }

                AttachPreparedEdge(
                    occurrence.Subject,
                    property,
                    occurrence.SubjectOrdinal,
                    occurrence.Index,
                    reservations,
                    notifier);
                if (!IsOwned(property.Subject))
                {
                    return;
                }
            }

            RefreshPreparedIndices(property, value, oldOccurrences, newOccurrences, newCounts, notifier);
        }
        finally
        {
            LifecycleScratch.Return(oldCounts);
            LifecycleScratch.Return(newCounts);
        }
    }

    private void AttachPreparedEdge(
        IInterceptorSubject subject,
        PropertyReference property,
        int subjectOrdinal,
        object? index,
        Dictionary<IInterceptorSubject, OwnershipReservationToken> reservations,
        LifecycleNotifier notifier)
    {
        var ownership = TryGetOwnership(subject);
        var isContextAttach = ownership is null;
        if (ownership is null)
        {
            if (!ReferenceEquals(subject.Executor.AttachedContext, Context))
            {
                throw LifecycleConflictException.Retryable(subject);
            }

            ownership = AddPreparedOwnership(subject);
        }

        ownership.AddIncoming(property, subjectOrdinal, index);
        ConsumePreparedAnchor(subject, property, reservations);
        var change = new SubjectLifecycleChange
        {
            Subject = subject,
            Property = property,
            Index = index,
            ReferenceCount = ownership.IncomingCount,
            IsContextAttach = isContextAttach,
            IsPropertyReferenceAdded = true
        };

        var properties = ownership.PropertyNames;
        notifier.InvokePreparedAddedLifecycleHandlers(
            subject,
            change,
            reservations,
            isContextAttach
                ? () => SeedAndAttachChildren(
                    subject, reservations, seed: !_isPreparing && !AreSnapshotsSeeded(subject), notifier)
                : null);
        if (isContextAttach)
        {
            notifier.RaiseSubjectAttached(change);
            notifier.AttachSubjectProperties(subject, properties);
        }
    }

    private SubjectOwnership AddPreparedOwnership(IInterceptorSubject subject) =>
        _isPreparing
            ? AddOwnership(subject, _preparedPropertyNames![subject])
            : AddOwnership(subject);

    private void ConsumePreparedAnchor(
        IInterceptorSubject subject,
        PropertyReference property,
        Dictionary<IInterceptorSubject, OwnershipReservationToken> reservations)
    {
        subject.Executor.TryGetAttachment(out var attachedContext, out var anchor, out _);
        if (anchor == SubjectAttachmentAnchorKind.Provisional && ReferenceEquals(attachedContext, Context) &&
            new ReachabilityWalk(this).IsAnchorReachable(property.Subject, subject))
        {
            SetAnchor(
                subject,
                SubjectAttachmentAnchorKind.None,
                SubjectAttachmentAnchorKind.Provisional,
                reservations.GetValueOrDefault(subject));
        }
    }

    private void RemovePreparedEdge(
        IInterceptorSubject subject,
        PropertyReference property,
        int subjectOrdinal,
        object? index,
        LifecycleNotifier notifier)
    {
        var ownership = TryGetOwnership(subject);
        if (ownership is null || !ownership.RemoveIncoming(property, subjectOrdinal))
        {
            return;
        }

        if (IsPreparedSubjectHeld(subject, ownership))
        {
            notifier.PublishEdgeRemoved(subject, property, index, ownership.IncomingCount);
        }
        else
        {
            ReleasePrepared(subject, ownership, property, index, notifier);
        }
    }

    private bool IsPreparedSubjectHeld(IInterceptorSubject subject, SubjectOwnership ownership) =>
        HasReservation(subject) || HasStructuralLease(subject) ||
        (ownership.IncomingCount > 0
            ? new ReachabilityWalk(this).IsAnchorReachable(subject, null)
            : IsAnchored(subject));

    private void ReleasePrepared(
        IInterceptorSubject subject,
        SubjectOwnership ownership,
        PropertyReference? property,
        object? index,
        LifecycleNotifier notifier)
    {
        var children = LifecycleScratch.RentChildList();
        try
        {
            CollectStructuralChildren(subject, children, seed: false);
            RemoveOwnership(subject);
            RemoveSnapshots(subject);
            MarkReleasing(subject);
            notifier.DetachSubjectProperties(subject, ownership.PropertyNames);
            DrainPreparedEdges(subject, ownership, notifier);

            var change = new SubjectLifecycleChange
            {
                Subject = subject,
                Property = property,
                Index = index,
                ReferenceCount = 0,
                IsPropertyReferenceRemoved = property.HasValue,
                IsContextDetach = true
            };
            notifier.RaiseSubjectDetaching(change);
            notifier.InvokeRemovedLifecycleHandlers(subject, change);
            ReleaseClaim(subject);
            ClearReleasing(subject);

            foreach (var (childProperty, occurrence) in children)
            {
                RemovePreparedEdge(
                    occurrence.Subject,
                    childProperty,
                    occurrence.SubjectOrdinal,
                    occurrence.Index,
                    notifier);
            }
        }
        finally
        {
            ClearReleasing(subject);
            LifecycleScratch.Return(children);
        }
    }

    private static void DrainPreparedEdges(
        IInterceptorSubject subject,
        SubjectOwnership ownership,
        LifecycleNotifier notifier)
    {
        var remaining = LifecycleScratch.RentEdgeList();
        try
        {
            ownership.CopyIncomingEdges(remaining);
            foreach (var edge in remaining)
            {
                ownership.RemoveIncoming(edge.Property, edge.SubjectOrdinal);
                notifier.PublishEdgeRemoved(subject, edge.Property, edge.Index, ownership.IncomingCount);
            }
        }
        finally
        {
            LifecycleScratch.Return(remaining);
        }
    }

    private void RefreshPreparedIndices(
        PropertyReference property,
        object? value,
        ImmutableArray<StructuralOccurrence> oldOccurrences,
        ImmutableArray<StructuralOccurrence> newOccurrences,
        Dictionary<IInterceptorSubject, int> newCounts,
        LifecycleNotifier notifier)
    {
        if (value is not IEnumerable || value is string || newOccurrences.IsEmpty ||
            !oldOccurrences.Any(occurrence =>
                occurrence.SubjectOrdinal < newCounts.GetValueOrDefault(occurrence.Subject)))
        {
            return;
        }

        var indicesBySubject = LifecycleScratch.RentIndexGroups();
        try
        {
            foreach (var occurrence in newOccurrences)
            {
                if (!indicesBySubject.TryGetValue(occurrence.Subject, out var indices))
                {
                    indices = LifecycleScratch.RentIndexList();
                    indicesBySubject.Add(occurrence.Subject, indices);
                }

                indices.Add(occurrence.Index);
            }

            foreach (var entry in indicesBySubject)
            {
                TryGetOwnership(entry.Key)?.UpdateIncomingIndices(property, entry.Value);
            }
        }
        finally
        {
            LifecycleScratch.Return(indicesBySubject);
        }

        notifier.RefreshCollectionProperty(property, value);
    }

    internal List<IInterceptorSubject>? PrepareDeferredSweep()
    {
        List<IInterceptorSubject>? result = null;
        foreach (var entry in _state.Owned)
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
        return _state.Snapshots.ContainsKey(property);
    }

    public bool ContainsOccurrence(PropertyReference property, IInterceptorSubject target, int subjectOrdinal)
    {
        if (!_state.Snapshots.TryGetValue(property, out var snapshot))
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
        if (!seed)
        {
            foreach (var entry in _state.Snapshots)
            {
                if (ReferenceEquals(entry.Key.Subject, subject))
                {
                    foreach (var occurrence in entry.Value.Occurrences)
                    {
                        children.Add((entry.Key, occurrence));
                    }
                }
            }

            return;
        }

        foreach (var entry in subject.Properties)
        {
            var metadata = entry.Value;
            if (!IsStructural(metadata))
            {
                continue;
            }

            var property = new PropertyReference(subject, entry.Key);
            var snapshot = StructuralSnapshotBuilder.Build(metadata.Type, metadata.GetValue?.Invoke(subject), 0);
            SetSnapshot(property, snapshot);

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
                return _state.Snapshots.ContainsKey(new PropertyReference(subject, entry.Key));
            }
        }

        return true;
    }

    public void RemoveSnapshots(IInterceptorSubject subject)
    {
        var state = _state;
        var snapshots = state.Snapshots.ToBuilder();
        foreach (var property in state.Snapshots.Keys)
        {
            if (ReferenceEquals(property.Subject, subject))
            {
                snapshots.Remove(property);
            }
        }

        _state = new GraphState(state.Owned, snapshots.ToImmutable());
    }

    internal void RefreshPropertyNames(IInterceptorSubject subject)
    {
        TryGetOwnership(subject)?.SetPropertyNames(subject.Properties.Keys.ToImmutableArray());
    }


    internal IDisposable ReserveForStructuralWrite(IInterceptorSubject subject)
    {
        return _coordinator.AcquireOwnershipReservation((InterceptorExecutor)subject.Executor, ReservationMode.Shared);
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
        if (_preparedAttachments?.TryGetValue(subject, out var target) == true)
        {
            return target.Context is not null && target.Anchor != SubjectAttachmentAnchorKind.None;
        }

        subject.Executor.TryGetAttachment(out var attachedContext, out var anchor, out _);
        return anchor != SubjectAttachmentAnchorKind.None && ReferenceEquals(attachedContext, Context);
    }

    public bool TryClaim(IInterceptorSubject subject, SubjectAttachmentAnchorKind anchor)
    {
        try
        {
            using var reservation = _coordinator.AcquireOwnershipReservation(
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
        if (_isPreparing)
        {
            RecordPreparedAttachment(subject, null, SubjectAttachmentAnchorKind.None, null);
            return;
        }

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

        if (_isPreparing)
        {
            subject.Executor.TryGetAttachment(out var preparedContext, out var preparedAnchor, out _);
            if (ReferenceEquals(preparedContext, Context) &&
                (onlyFrom is null ? preparedAnchor != anchor : preparedAnchor == onlyFrom))
            {
                RecordPreparedAttachment(
                    subject,
                    (InterceptorSubjectContext)Context,
                    anchor,
                    reservation);
            }

            return;
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

    private void RecordPreparedAttachment(
        IInterceptorSubject subject,
        InterceptorSubjectContext? context,
        SubjectAttachmentAnchorKind anchor,
        OwnershipReservationToken? reservation)
    {
        (_preparedAttachments ??= new(ReferenceEqualityComparer.Instance))[subject] =
            new PreparedAttachmentTarget(context, anchor, reservation);
    }

    private sealed record PreparedAttachmentTarget(InterceptorSubjectContext? Context,
        SubjectAttachmentAnchorKind Anchor,
        OwnershipReservationToken? Reservation);


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
        Dictionary<PropertyReference, StructuralSnapshot>? seededSnapshots = null,
        Dictionary<IInterceptorSubject, ImmutableArray<string>>? seededPropertyNames = null)
    {
        foreach (var occurrence in snapshot.Occurrences)
        {
            DiscoverComponent(
                occurrence.Subject,
                visited,
                discovered,
                includeAttached,
                seededSnapshots,
                seededPropertyNames);
        }
    }

    public void DiscoverComponent(
        IInterceptorSubject start,
        HashSet<IInterceptorSubject> visited,
        List<IInterceptorSubject> discovered,
        bool includeAttached = false,
        Dictionary<PropertyReference, StructuralSnapshot>? seededSnapshots = null,
        Dictionary<IInterceptorSubject, ImmutableArray<string>>? seededPropertyNames = null)
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

                var propertyNames = seededPropertyNames is null
                    ? null
                    : ImmutableArray.CreateBuilder<string>(subject.Properties.Count);
                foreach (var entry in subject.Properties)
                {
                    propertyNames?.Add(entry.Key);
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

                if (propertyNames is not null)
                {
                    seededPropertyNames!.Add(subject, propertyNames.MoveToImmutable());
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

}
