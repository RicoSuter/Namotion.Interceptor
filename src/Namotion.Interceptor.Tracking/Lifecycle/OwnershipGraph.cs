using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Tracking.Lifecycle;

internal sealed class OwnershipGraph
{
    internal sealed record GraphState(ImmutableDictionary<IInterceptorSubject, SubjectOwnership> Owned,
        ImmutableDictionary<PropertyReference, StructuralSnapshot> Snapshots,
        bool DeferredSweep)
    {
        internal static readonly GraphState Empty = new(
            ImmutableDictionary.Create<IInterceptorSubject, SubjectOwnership>(ReferenceEqualityComparer.Instance),
            ImmutableDictionary.Create<PropertyReference, StructuralSnapshot>(PropertyReference.Comparer),
            false);
    }

    private readonly ITopologyAdmissionCoordinator _coordinator;
    private volatile GraphState _state;
    private Dictionary<IInterceptorSubject, PreparedAttachmentTarget>? _preparedAttachments;
    private Dictionary<IInterceptorSubject, CapturedSubjectProperties>? _preparedSubjectProperties;
    public IInterceptorSubjectContext Context { get; }
    internal GraphState State => _state;
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
        _state = source._state;
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

    private SubjectOwnership AddOwnership(
        IInterceptorSubject subject,
        CapturedSubjectProperties properties,
        InterceptorExecutor executor)
    {
        var ownership = new SubjectOwnership(properties, executor);
        var state = _state;
        _state = new GraphState(state.Owned.SetItem(subject, ownership), state.Snapshots, state.DeferredSweep);
        return ownership;
    }

    private void RemoveOwnership(IInterceptorSubject subject)
    {
        var state = _state;
        _state = new GraphState(state.Owned.Remove(subject), state.Snapshots, state.DeferredSweep);
    }

    private void SetOwnership(IInterceptorSubject subject, SubjectOwnership ownership)
    {
        var state = _state;
        _state = new GraphState(state.Owned.SetItem(subject, ownership), state.Snapshots, state.DeferredSweep);
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
    private void SetSnapshot(PropertyReference property, StructuralSnapshot snapshot)
    {
        var state = _state;
        _state = new GraphState(state.Owned, state.Snapshots.SetItem(property, snapshot), state.DeferredSweep);
    }

    internal sealed class PreparedTopologyChange(
        OwnershipGraph owner,
        GraphState publication,
        ImmutableArray<InterceptorExecutor.AttachmentTransition> attachmentUpdates,
        bool refreshCollection = false) : IDisposable
    {
        private bool _isPublished;

        internal bool RefreshCollection { get; } = refreshCollection;

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
        StructuralSnapshot capturedSnapshot,
        long revision,
        Dictionary<PropertyReference, StructuralSnapshot> seededSnapshots,
        Dictionary<IInterceptorSubject, CapturedSubjectProperties> seededSubjectProperties,
        Dictionary<IInterceptorSubject, OwnershipReservationToken> reservations,
        LifecycleNotifier notifier)
        => PrepareReconcile(
            property,
            capturedSnapshot with { SourceRevision = revision },
            seededSnapshots,
            seededSubjectProperties,
            reservations,
            notifier);

    internal void Publish(PreparedTopologyChange change) => change.Publish();

    internal bool IsCaptureCurrent(
        ImmutableArray<StructuralSnapshotBuilder.CaptureParticipant> participants)
    {
        var state = _state;
        foreach (var participant in participants)
        {
            if (!participant.IsLocallyCurrent())
            {
                return false;
            }

            var isOwned = state.Owned.TryGetValue(participant.Subject, out var ownership);
            if (isOwned != (participant.Ownership is not null) ||
                isOwned && !ReferenceEquals(ownership, participant.Ownership))
            {
                return false;
            }
        }

        return true;
    }

    internal PreparedTopologyChange PrepareReconcile(
        PropertyReference property,
        StructuralSnapshot snapshot,
        Dictionary<PropertyReference, StructuralSnapshot> seededSnapshots,
        Dictionary<IInterceptorSubject, CapturedSubjectProperties> seededSubjectProperties,
        Dictionary<IInterceptorSubject, OwnershipReservationToken> reservations,
        LifecycleNotifier notifier)
    {
        var baseline = _state;
        if (!baseline.Owned.ContainsKey(property.Subject))
        {
            throw LifecycleConflictException.Retryable(property.Subject);
        }

        var prepared = new OwnershipGraph(this) { _preparedSubjectProperties = seededSubjectProperties };
        foreach (var entry in seededSnapshots)
        {
            prepared.SetSnapshot(entry.Key, entry.Value);
        }

        prepared.SetSnapshot(property, snapshot);
        var refreshCollection = prepared.ReconcilePrepared(
            property,
            baseline.Snapshots.GetValueOrDefault(property, StructuralSnapshot.Empty).Occurrences,
            snapshot.Occurrences,
            reservations,
            notifier);
        if (refreshCollection)
        {
            notifier.RefreshCollectionProperty(property, prepared.GetSnapshot(property), prepared._state);
        }

        return new PreparedTopologyChange(
            this,
            prepared._state,
            prepared.PrepareAttachmentUpdates(reservations),
            refreshCollection);
    }

    internal PreparedTopologyChange PrepareAdmission(
        IInterceptorSubject subject,
        ImmutableArray<PropertyReference> structuralProperties,
        Dictionary<PropertyReference, StructuralSnapshot> capturedSnapshots,
        Dictionary<IInterceptorSubject, CapturedSubjectProperties> capturedSubjectProperties,
        Dictionary<IInterceptorSubject, OwnershipReservationToken> reservations,
        LifecycleNotifier notifier)
    {
        if (_state.Owned.TryGetValue(subject, out var ownership) is false)
        {
            throw LifecycleConflictException.Retryable(subject);
        }

        var prepared = new OwnershipGraph(this) { _preparedSubjectProperties = capturedSubjectProperties };
        foreach (var entry in capturedSnapshots)
        {
            prepared.SetSnapshot(entry.Key, entry.Value);
        }

        var properties = capturedSubjectProperties[subject];
        prepared.SetOwnership(subject, ownership with
        {
            PropertyNames = properties.Names,
            Properties = properties.Metadata,
            LifecycleHandler = properties.LifecycleHandler,
            PropertyHandler = properties.PropertyHandler
        });
        foreach (var property in structuralProperties)
        {
            prepared.ReconcilePrepared(
                property,
                [],
                capturedSnapshots[property].Occurrences,
                reservations,
                notifier);
        }

        return new PreparedTopologyChange(
            this,
            prepared._state,
            prepared.PrepareAttachmentUpdates(reservations));
    }

    internal PreparedTopologyChange PrepareAttach(
        IInterceptorSubject root,
        SubjectAttachmentAnchorKind anchor,
        Dictionary<PropertyReference, StructuralSnapshot> capturedSnapshots,
        Dictionary<IInterceptorSubject, CapturedSubjectProperties> capturedSubjectProperties,
        Dictionary<IInterceptorSubject, OwnershipReservationToken> reservations,
        LifecycleNotifier notifier)
    {
        var prepared = new OwnershipGraph(this) { _preparedSubjectProperties = capturedSubjectProperties };
        foreach (var entry in capturedSnapshots)
        {
            prepared.SetSnapshot(entry.Key, entry.Value);
        }

        foreach (var entry in reservations)
        {
            var executor = entry.Value.Executor;
            executor.TryGetAttachment(out _, out var currentAnchor, out _);
            prepared.RecordPreparedAttachment(
                entry.Key,
                executor,
                (InterceptorSubjectContext)Context,
                ReferenceEquals(entry.Key, root) ? anchor : currentAnchor,
                entry.Value);
        }

        if (!prepared.IsOwned(root))
        {
            prepared.SeedPreparedChildren(root, reservations, notifier);
            if (!prepared.IsOwned(root))
            {
                prepared.AttachPreparedRoot(root, reservations[root].Executor, notifier);
            }
        }
        else
        {
            prepared.SetAnchor(root, anchor);
        }

        return new PreparedTopologyChange(
            this,
            prepared._state,
            prepared.PrepareAttachmentUpdates(reservations));
    }

    internal PreparedTopologyChange PrepareDetach(
        IInterceptorSubject subject, InterceptorExecutor executor, LifecycleNotifier notifier)
    {
        var prepared = new OwnershipGraph(this);
        prepared.SetAnchor(subject, SubjectAttachmentAnchorKind.None, executor: executor);
        if (prepared.TryGetOwnership(subject) is not { } ownership)
        {
            prepared.ReleaseClaim(subject, executor);
        }
        else if (!prepared.IsReachableFromRoot(subject, null, includeProtectors: true))
        {
            prepared.ReleasePrepared(subject, ownership, null, null, notifier);
        }
        else if (!prepared.IsReachableFromRoot(subject, null, includeProtectors: false))
        {
            prepared.MarkDeferredSweep();
        }

        return new PreparedTopologyChange(
            this,
            prepared._state,
            prepared.PrepareAttachmentUpdates(null));
    }

    private void SeedPreparedChildren(
        IInterceptorSubject subject,
        Dictionary<IInterceptorSubject, OwnershipReservationToken>? reservations,
        LifecycleNotifier notifier)
    {
        var children = LifecycleScratch.RentChildList();
        try
        {
            CollectStructuralChildren(subject, children);
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

    private void AttachPreparedRoot(
        IInterceptorSubject subject, InterceptorExecutor executor, LifecycleNotifier notifier)
    {
        var ownership = AddPreparedOwnership(subject, executor);
        var change = new SubjectLifecycleChange
        {
            Subject = subject,
            ReferenceCount = 0,
            IsContextAttach = true
        };
        change = notifier.CompleteChange(change, ownership.Properties, ownership.Parents, StructuralSnapshot.Empty);
        notifier.InvokeAddedLifecycleHandlers(subject, ownership.LifecycleHandler, executor, change);
        notifier.RaiseSubjectAttached(change, executor);
        notifier.AttachSubjectProperties(
            subject, ownership.PropertyHandler, executor, ownership.Properties, _state.Snapshots, _state);
    }

    private ImmutableArray<InterceptorExecutor.AttachmentTransition> PrepareAttachmentUpdates(
        Dictionary<IInterceptorSubject, OwnershipReservationToken>? reservations)
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
                target.Executor.TryGetAttachment(out var currentContext, out var currentAnchor, out _);
                if (ReferenceEquals(currentContext, target.Context) && currentAnchor == target.Anchor)
                {
                    continue;
                }

                OwnershipReservationToken? reservation = null;
                reservations?.TryGetValue(subject, out reservation);
                updates.Add(target.Executor.PrepareAttachmentUpdate(
                    (InterceptorSubjectContext?)currentContext,
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

    private bool ReconcilePrepared(
        PropertyReference property,
        ImmutableArray<StructuralOccurrence> oldOccurrences,
        ImmutableArray<StructuralOccurrence> newOccurrences,
        Dictionary<IInterceptorSubject, OwnershipReservationToken> reservations,
        LifecycleNotifier notifier)
    {
        var oldCounts = LifecycleScratch.RentSubjectCounter();
        var newCounts = LifecycleScratch.RentSubjectCounter();
        var retainedCounts = LifecycleScratch.RentSubjectCounter();
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

            foreach (var entry in oldCounts)
            {
                retainedCounts[entry.Key] = Math.Min(entry.Value, newCounts.GetValueOrDefault(entry.Key));
            }

            var additionEntryStart = notifier.JournalEntryCount;
            foreach (var occurrence in newOccurrences)
            {
                var retained = retainedCounts.GetValueOrDefault(occurrence.Subject);
                if (retained > 0)
                {
                    retainedCounts[occurrence.Subject] = retained - 1;
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
                    return false;
                }
            }

            var deferredAdditions = notifier.DeferJournalEntriesFrom(additionEntryStart);
            try
            {
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
                        return false;
                    }
                }
            }
            finally
            {
                notifier.AppendJournalEntries(deferredAdditions);
            }

            return RefreshPreparedIndices(property, oldOccurrences, newOccurrences, newCounts);
        }
        finally
        {
            LifecycleScratch.Return(oldCounts);
            LifecycleScratch.Return(newCounts);
            LifecycleScratch.Return(retainedCounts);
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
        if (ownership?.ContainsIncoming(property, subjectOrdinal) == true)
        {
            return;
        }

        var reservation = reservations.GetValueOrDefault(subject)
            ?? throw LifecycleConflictException.Retryable(subject);
        var executor = reservation.Executor;
        if (ownership is null)
        {
            if (!executor.IsOwnershipReservationActive(reservation, (InterceptorSubjectContext)Context))
            {
                throw LifecycleConflictException.Retryable(subject);
            }

            if (executor.AttachedContext is null &&
                _preparedAttachments?.ContainsKey(subject) != true)
            {
                RecordPreparedAttachment(
                    subject,
                    executor,
                    (InterceptorSubjectContext)Context,
                    SubjectAttachmentAnchorKind.None,
                    reservation);
            }

            ownership = AddPreparedOwnership(subject, executor);
        }

        ownership = ownership.AddIncoming(property, subjectOrdinal, index);
        SetOwnership(subject, ownership);
        ConsumePreparedAnchor(subject, property, executor, reservation);
        var change = new SubjectLifecycleChange
        {
            Subject = subject,
            Property = property,
            Index = index,
            ReferenceCount = ownership.IncomingCount,
            IsContextAttach = isContextAttach,
            IsPropertyReferenceAdded = true
        };
        change = notifier.CompleteChange(
            change, ownership.Properties, ownership.Parents, GetSnapshot(property));

        notifier.InvokePreparedAddedLifecycleHandlers(
            subject,
            ownership.LifecycleHandler,
            executor,
            change,
            isContextAttach
                ? () => SeedPreparedChildren(subject, reservations, notifier)
                : null);
        if (isContextAttach)
        {
            notifier.RaiseSubjectAttached(change, executor);
            notifier.AttachSubjectProperties(
                subject, ownership.PropertyHandler, executor, ownership.Properties, _state.Snapshots, _state);
        }
    }

    private SubjectOwnership AddPreparedOwnership(IInterceptorSubject subject, InterceptorExecutor executor) =>
        AddOwnership(subject, _preparedSubjectProperties![subject], executor);

    private void ConsumePreparedAnchor(
        IInterceptorSubject subject, PropertyReference property, InterceptorExecutor executor,
        OwnershipReservationToken reservation)
    {
        executor.TryGetAttachment(out var attachedContext, out var anchor, out _);
        if (anchor == SubjectAttachmentAnchorKind.Provisional && ReferenceEquals(attachedContext, Context) &&
            IsReachableFromRoot(property.Subject, subject, includeProtectors: false))
        {
            SetAnchor(
                subject,
                SubjectAttachmentAnchorKind.None,
                SubjectAttachmentAnchorKind.Provisional,
                reservation);
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
        if (ownership is null || !ownership.TryRemoveIncoming(property, subjectOrdinal, out ownership))
        {
            return;
        }

        SetOwnership(subject, ownership);

        if (IsPreparedSubjectHeld(subject))
        {
            var change = notifier.CompleteChange(new SubjectLifecycleChange
            {
                Subject = subject,
                Property = property,
                Index = index,
                ReferenceCount = ownership.IncomingCount,
                IsPropertyReferenceRemoved = true
            }, ownership.Properties, ownership.Parents, GetSnapshot(property));
            notifier.PublishEdgeRemoved(subject, ownership.LifecycleHandler, change);
        }
        else
        {
            ReleasePrepared(subject, ownership, property, index, notifier);
        }
    }

    private bool IsPreparedSubjectHeld(IInterceptorSubject subject)
    {
        if (IsReachableFromRoot(subject, null, includeProtectors: false))
        {
            return true;
        }

        if (IsReachableFromRoot(subject, null, includeProtectors: true))
        {
            SetDeferredSweep(true);
            return true;
        }

        return false;
    }

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
            CollectStructuralChildren(subject, children);
            RemoveOwnership(subject);
            RemoveSnapshots(subject);
            notifier.DetachSubjectProperties(subject, ownership.PropertyHandler, ownership.Properties);
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
            change = notifier.CompleteChange(
                change, ownership.Properties, [],
                property is { } parentProperty ? GetSnapshot(parentProperty) : StructuralSnapshot.Empty);
            notifier.RaiseSubjectDetaching(change);
            notifier.InvokeRemovedLifecycleHandlers(subject, ownership.LifecycleHandler, change);
            ReleaseClaim(subject, ownership.Executor!);

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
            LifecycleScratch.Return(children);
        }
    }

    private void DrainPreparedEdges(
        IInterceptorSubject subject,
        SubjectOwnership ownership,
        LifecycleNotifier notifier)
    {
        var referenceCount = ownership.IncomingCount;
        foreach (var edge in ownership.Edges)
        {
            var change = notifier.CompleteChange(new SubjectLifecycleChange
            {
                Subject = subject,
                Property = edge.Property,
                Index = edge.Index,
                ReferenceCount = --referenceCount,
                IsPropertyReferenceRemoved = true
            }, ownership.Properties, [], GetSnapshot(edge.Property));
            notifier.PublishEdgeRemoved(subject, ownership.LifecycleHandler, change);
        }
    }

    private bool RefreshPreparedIndices(
        PropertyReference property,
        ImmutableArray<StructuralOccurrence> oldOccurrences,
        ImmutableArray<StructuralOccurrence> newOccurrences,
        Dictionary<IInterceptorSubject, int> newCounts)
    {
        if (!newOccurrences.Any(static occurrence => occurrence.Index is not null) ||
            !oldOccurrences.Any(occurrence =>
                occurrence.SubjectOrdinal < newCounts.GetValueOrDefault(occurrence.Subject)))
        {
            return false;
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
                if (TryGetOwnership(entry.Key) is { } ownership)
                {
                    SetOwnership(entry.Key, ownership.UpdateIncomingIndices(property, entry.Value));
                }
            }
        }
        finally
        {
            LifecycleScratch.Return(indicesBySubject);
        }

        return true;
    }

    internal bool HasDeferredSweep => _state.DeferredSweep;

    private void MarkDeferredSweep() => SetDeferredSweep(true);

    private void SetDeferredSweep(bool value)
    {
        var state = _state;
        _state = new GraphState(state.Owned, state.Snapshots, value);
    }

    internal PreparedTopologyChange? PrepareDeferredSweep(LifecycleNotifier notifier)
    {
        if (!_state.DeferredSweep)
        {
            return null;
        }

        var prepared = new OwnershipGraph(this);
        var outgoing = new Dictionary<IInterceptorSubject, List<IInterceptorSubject>>(
            ReferenceEqualityComparer.Instance);
        foreach (var entry in prepared._state.Snapshots)
        {
            if (!prepared.IsOwned(entry.Key.Subject))
            {
                continue;
            }

            if (!outgoing.TryGetValue(entry.Key.Subject, out var children))
            {
                children = [];
                outgoing.Add(entry.Key.Subject, children);
            }

            foreach (var occurrence in entry.Value.Occurrences)
            {
                if (prepared.IsOwned(occurrence.Subject))
                {
                    children.Add(occurrence.Subject);
                }
            }
        }

        var reachable = LifecycleScratch.RentSubjectSet();
        var pending = LifecycleScratch.RentSubjectStack();
        try
        {
            foreach (var subject in prepared._state.Owned.Keys)
            {
                if (prepared.IsAnchored(subject))
                {
                    pending.Push(subject);
                }
            }

            MarkReachable(outgoing, reachable, pending);
            var anchorReachableCount = reachable.Count;

            foreach (var subject in prepared._state.Owned.Keys)
            {
                if (prepared.HasProtector(subject))
                {
                    pending.Push(subject);
                }
            }

            MarkReachable(outgoing, reachable, pending);
            prepared.SetDeferredSweep(reachable.Count > anchorReachableCount);

            foreach (var subject in prepared._state.Owned.Keys.ToArray())
            {
                if (!reachable.Contains(subject) && prepared.TryGetOwnership(subject) is { } ownership)
                {
                    prepared.ReleasePrepared(subject, ownership, null, null, notifier);
                }
            }

            return new PreparedTopologyChange(
                this,
                prepared._state,
                prepared.PrepareAttachmentUpdates(null));
        }
        finally
        {
            LifecycleScratch.Return(reachable);
            LifecycleScratch.Return(pending);
        }
    }

    private static void MarkReachable(
        Dictionary<IInterceptorSubject, List<IInterceptorSubject>> outgoing,
        HashSet<IInterceptorSubject> reachable,
        Stack<IInterceptorSubject> pending)
    {
        while (pending.Count > 0)
        {
            var subject = pending.Pop();
            if (!reachable.Add(subject) || !outgoing.TryGetValue(subject, out var children))
            {
                continue;
            }

            foreach (var child in children)
            {
                pending.Push(child);
            }
        }
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

    private void CollectStructuralChildren(
        IInterceptorSubject subject,
        List<(PropertyReference Property, StructuralOccurrence Occurrence)> children)
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
    }

    private void RemoveSnapshots(IInterceptorSubject subject)
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

        _state = new GraphState(state.Owned, snapshots.ToImmutable(), state.DeferredSweep);
    }

    internal IDisposable ReserveForStructuralWrite(InterceptorExecutor executor, ReservationMode mode = ReservationMode.Shared) =>
        _coordinator.AcquireOwnershipReservation(executor, mode, false);

    internal bool HasReservation(IInterceptorSubject subject) =>
        TryGetOwnership(subject)?.Executor?.HasOwnershipReservation((InterceptorSubjectContext)Context) == true;

    internal bool HasProtector(IInterceptorSubject subject) =>
        HasReservation(subject) ||
        TryGetOwnership(subject)?.Executor?.HasStructuralWriteLease((InterceptorSubjectContext)Context) == true;

    internal bool IsReachableFromRoot(
        IInterceptorSubject start,
        IInterceptorSubject? excluded,
        bool includeProtectors)
    {
        if (IsReachabilityRoot(start, excluded, includeProtectors))
        {
            return true;
        }

        var current = start;
        for (var step = 0; step < 8; step++)
        {
            var ownership = TryGetOwnership(current);
            if (ownership is null || ownership.IncomingCount == 0)
            {
                return false;
            }

            if (!ownership.TryGetSingleIncoming(out var incoming))
            {
                break;
            }

            if (!ContainsOccurrence(incoming.Property, current, incoming.SubjectOrdinal))
            {
                return false;
            }

            var parent = incoming.Property.Subject;
            if (ReferenceEquals(parent, start))
            {
                return false;
            }

            if (IsReachabilityRoot(parent, excluded, includeProtectors))
            {
                return true;
            }

            current = parent;
        }

        var visited = LifecycleScratch.RentSubjectSet();
        var pending = LifecycleScratch.RentSubjectStack();
        var edges = LifecycleScratch.RentEdgeList();
        try
        {
            visited.Add(current);
            pending.Push(current);
            while (pending.Count > 0)
            {
                var subject = pending.Pop();
                edges.Clear();
                TryGetOwnership(subject)?.CopyIncomingEdges(edges);
                foreach (var edge in edges)
                {
                    var parent = edge.Property.Subject;
                    if (visited.Contains(parent) ||
                        !ContainsOccurrence(edge.Property, subject, edge.SubjectOrdinal))
                    {
                        continue;
                    }

                    visited.Add(parent);
                    if (IsReachabilityRoot(parent, excluded, includeProtectors))
                    {
                        return true;
                    }

                    pending.Push(parent);
                }
            }

            return false;
        }
        finally
        {
            LifecycleScratch.Return(visited);
            LifecycleScratch.Return(pending);
            LifecycleScratch.Return(edges);
        }
    }

    private bool IsReachabilityRoot(
        IInterceptorSubject subject,
        IInterceptorSubject? excluded,
        bool includeProtectors) =>
        !ReferenceEquals(subject, excluded) &&
        (IsAnchored(subject) || includeProtectors && HasProtector(subject));

    internal void ReleaseUnusedReservation(IDisposable participant)
    {
        var reservation = (OwnershipReservationToken)participant;
        var executor = reservation.Executor;
        var subject = executor.Subject;
        executor.TryGetAttachment(out var attachedContext, out var anchor, out _);
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

        if (TryGetOwnership(subject)?.Executor is not { } executor) return false;

        executor.TryGetAttachment(out var attachedContext, out var anchor, out _);
        return anchor != SubjectAttachmentAnchorKind.None && ReferenceEquals(attachedContext, Context);
    }

    private void ReleaseClaim(IInterceptorSubject subject, InterceptorExecutor executor) =>
        RecordPreparedAttachment(subject, executor, null, SubjectAttachmentAnchorKind.None, null);

    private void SetAnchor(
        IInterceptorSubject subject,
        SubjectAttachmentAnchorKind anchor,
        SubjectAttachmentAnchorKind? onlyFrom = null,
        OwnershipReservationToken? reservation = null,
        InterceptorExecutor? executor = null)
    {
        if (reservation is not null && !ReferenceEquals(reservation.Executor.Subject, subject))
        {
            throw new InvalidOperationException("The ownership reservation belongs to a different subject.");
        }

        executor ??= reservation?.Executor ?? TryGetOwnership(subject)?.Executor;
        if (executor is null) return;

        executor.TryGetAttachment(out var preparedContext, out var preparedAnchor, out _);
        if (ReferenceEquals(preparedContext, Context) &&
            (onlyFrom is null ? preparedAnchor != anchor : preparedAnchor == onlyFrom))
        {
            RecordPreparedAttachment(
                subject,
                executor,
                (InterceptorSubjectContext)Context,
                anchor,
                reservation);
        }
    }

    private void RecordPreparedAttachment(
        IInterceptorSubject subject, InterceptorExecutor executor, InterceptorSubjectContext? context,
        SubjectAttachmentAnchorKind anchor, OwnershipReservationToken? reservation)
    {
        (_preparedAttachments ??= new(ReferenceEqualityComparer.Instance))[subject] =
            new PreparedAttachmentTarget(executor, context, anchor, reservation);
    }

    private sealed record PreparedAttachmentTarget(
        InterceptorExecutor Executor, InterceptorSubjectContext? Context,
        SubjectAttachmentAnchorKind Anchor, OwnershipReservationToken? Reservation);


    public bool TryReserveParticipants(
        ImmutableArray<StructuralSnapshotBuilder.CaptureParticipant> participants,
        Dictionary<IInterceptorSubject, OwnershipReservationToken> reservations,
        IInterceptorSubject? exclusiveRoot = null,
        IInterceptorSubject? joinExclusiveRoot = null)
    {
        foreach (var participant in participants)
        {
            var subject = participant.Subject;
            if (reservations.ContainsKey(subject))
            {
                continue;
            }

            try
            {
                var mode = ReferenceEquals(subject, exclusiveRoot)
                    ? ReservationMode.Exclusive
                    : ReservationMode.Shared;
                var reservation = _coordinator.AcquireOwnershipReservation(
                    participant.Executor,
                    mode,
                    ReferenceEquals(subject, joinExclusiveRoot));
                reservations.Add(subject, reservation);
            }
            catch (LifecycleConflictException)
            {
                ReleaseUnusedReservations(reservations);
                var attachedContext = participant.Executor.AttachedContext;
                if (attachedContext is not null && !ReferenceEquals(attachedContext, Context))
                {
                    return false;
                }

                throw;
            }
        }

        return true;
    }

    public void ReleaseUnusedReservations(Dictionary<IInterceptorSubject, OwnershipReservationToken> reservations)
    {
        foreach (var reservation in reservations.Values)
        {
            ReleaseUnusedReservation(reservation);
        }

        reservations.Clear();
    }

}
