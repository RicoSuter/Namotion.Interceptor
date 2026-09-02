using System.Collections.Immutable;
using System.Diagnostics;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>Owns structural graph membership and lifecycle publication for one context.</summary>
public sealed class LifecycleInterceptor : ILifecycleInterceptor, ILifecycleHandler,
    IWriteTerminalCoordinator, ITopologyAdmissionCoordinator,
    INonStructuralWriteCompletionCoordinator, ILogicalContextGuard
{
    private readonly IInterceptorSubjectContext _context;
    private readonly OwnershipGraph _graph;
    private readonly LifecycleNotifier _notifier;
    private readonly PropertyAdmission _admission;

    private readonly Lock _gate = new();

    [ThreadStatic]
    private static int _heldGateCount;

    /// <summary>Raised after a subject attaches to the object graph.</summary>
    public event Action<SubjectLifecycleChange>? SubjectAttached
    {
        add => _notifier.SubjectAttached += value;
        remove => _notifier.SubjectAttached -= value;
    }

    /// <summary>Raised when a subject detaches from the object graph.</summary>
    public event Action<SubjectLifecycleChange>? SubjectDetaching
    {
        add => _notifier.SubjectDetaching += value;
        remove => _notifier.SubjectDetaching -= value;
    }

    /// <summary>Creates the lifecycle authority for one exact context.</summary>
    public LifecycleInterceptor(IInterceptorSubjectContext context)
    {
        _context = context;
        _notifier = new LifecycleNotifier(context, this);
        _graph = new OwnershipGraph(context, this);
        _admission = new PropertyAdmission(_notifier, _graph);
    }

    private GateScope EnterGate()
    {
        if (_heldGateCount > 0 && !_gate.IsHeldByCurrentThread)
        {
            throw new LifecycleContractViolationException(
                "A thread runs at most one lifecycle topology transaction at a time, and this one " +
                "is already inside a transaction of another context. Topology gates have no order " +
                "among themselves, so waiting for a second one can deadlock against a thread " +
                "taking them the other way round. Nothing was read and nothing was changed: defer " +
                "the second operation until the enclosing one completes.");
        }

        _gate.Enter();
        _heldGateCount++;

        return new GateScope(this);
    }

    private void ExitGate()
    {
        _heldGateCount--;
        _gate.Exit();
    }

    private readonly struct GateScope(LifecycleInterceptor lifecycle) : IDisposable
    {
        public void Dispose()
        {
            lifecycle.ExitGate();
        }

    }

    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        var property = context.Property;
        var metadata = property.Metadata;
        if (!metadata.Type.CanContainSubjects<TProperty>() || !metadata.IsIntercepted ||
            metadata is { IsDerived: true, IsDynamic: true, SetValue: null })
        {
            next(ref context);
            return;
        }

        LifecycleNotifier.ThrowIfTopologyChange((InterceptorSubjectContext)_context);

        if (!ReferenceEquals(context.Executor.AttachedContext, _context))
        {
            next(ref context);
            return;
        }

        context.TerminalCoordinator = this;
        Exception? primaryException = null;
        try
        {
            next(ref context);
        }
        catch (Exception exception)
        {
            primaryException = exception;
        }

        if (context.CommittedLifecycleJournal is LifecycleNotifier.LifecycleJournal journal)
        {
            primaryException = journal.Drain(primaryException);
        }

        if (primaryException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primaryException).Throw();
        }
    }

    void IWriteTerminalCoordinator.ExecuteTerminal<TProperty>(
        ref PropertyWriteContext<TProperty> context,
        Func<IInterceptorSubject, TProperty>? readValue,
        Action<IInterceptorSubject, TProperty> writeValue)
    {
        if (readValue is null || context.StructuralLease is not { } lease)
        {
            throw new InvalidOperationException("A coordinated structural terminal requires its trusted raw reader and active lease.");
        }

        var value = context.GetFinalValue();
        var snapshot = StructuralSnapshotBuilder.Build(context.Property.Metadata.Type, value, 0);
        var reservations = LifecycleScratch.RentOwnershipReservations();
        var seededSnapshots = new Dictionary<PropertyReference, StructuralSnapshot>(PropertyReference.Comparer);
        var seededSubjectProperties = new Dictionary<IInterceptorSubject, ImmutableArray<SubjectPropertyMetadata>>(ReferenceEqualityComparer.Instance);
        try
        {
            while (true)
            {
                ImmutableArray<StructuralSnapshotBuilder.CaptureParticipant> participants;
                try
                {
                    participants = ReserveComponent(
                        snapshot, reservations, seededSnapshots, seededSubjectProperties);
                }
                catch (LifecycleConflictException exception) when (exception.IsTransientCapture)
                {
                    ResetCaptureAttempt(reservations, seededSnapshots, seededSubjectProperties);
                    continue;
                }
                var retryCapture = false;
                using (var journalCapture = _notifier.BeginJournal())
                {
                    lock (context.Executor.SyncRoot)
                    {
                        context.SetTerminalPredecessor(readValue(context.Property.Subject));
                        using (EnterGate())
                        {
                            if (!context.Executor.IsStructuralWriteLeaseActive(
                                    lease, (InterceptorSubjectContext)_context) ||
                                reservations.Values.Any(reservation =>
                                    !reservation.IsActive((InterceptorSubjectContext)_context)))
                            {
                                throw LifecycleConflictException.Retryable(context.Property.Subject);
                            }

                            if (!_graph.IsCaptureCurrent(participants))
                            {
                                retryCapture = true;
                            }
                            else
                            {
                                using var change = _graph.PrepareWrite(
                                    context.Property,
                                    snapshot,
                                    context.Executor.Revision + 1,
                                    seededSnapshots,
                                    seededSubjectProperties,
                                    reservations,
                                    _notifier);
                                journalCapture.PreflightCompletion();
                                var journal = journalCapture.CompleteAfterPreflight();
                                context.Executor.CommitRawWriteLocked(ref context, value, writeValue);
                                if (context.IsTerminalCommitted)
                                {
                                    _graph.Publish(change);
                                    context.CommittedLifecycleJournal = journal;
                                }
                            }
                        }
                    }
                }

                if (!retryCapture)
                {
                    break;
                }

                ResetCaptureAttempt(
                    reservations, seededSnapshots, seededSubjectProperties);
            }
        }
        finally
        {
            _graph.ReleaseUnusedReservations(reservations);
            LifecycleScratch.Return(reservations);
        }
    }

    private ImmutableArray<StructuralSnapshotBuilder.CaptureParticipant> ReserveComponent(
        StructuralSnapshot snapshot,
        Dictionary<IInterceptorSubject, OwnershipReservationToken> reservations,
        Dictionary<PropertyReference, StructuralSnapshot> seededSnapshots,
        Dictionary<IInterceptorSubject, ImmutableArray<SubjectPropertyMetadata>> seededSubjectProperties)
    {
        var visited = LifecycleScratch.RentSubjectSet();
        try
        {
            var participants = StructuralSnapshotBuilder.CaptureComponent(
                snapshot,
                _context,
                _graph.State,
                visited,
                seededSnapshots,
                seededSubjectProperties);

            if (!_graph.TryReserveParticipants(participants, reservations))
            {
                throw new InvalidOperationException(
                    "Another context claimed a subject of the assigned graph before its structural write reached the terminal.");
            }

            return participants;
        }
        finally
        {
            LifecycleScratch.Return(visited);
        }
    }

    private void ResetCaptureAttempt(
        Dictionary<IInterceptorSubject, OwnershipReservationToken> reservations,
        Dictionary<PropertyReference, StructuralSnapshot> snapshots,
        Dictionary<IInterceptorSubject, ImmutableArray<SubjectPropertyMetadata>> subjectProperties)
    {
        _graph.ReleaseUnusedReservations(reservations);
        snapshots.Clear();
        subjectProperties.Clear();
    }

    StructuralWriteLease ITopologyAdmissionCoordinator.AcquireStructuralWriteLease(InterceptorExecutor executor)
    {
        using (EnterGate())
        {
            return executor.TryAcquireStructuralWriteLease((InterceptorSubjectContext)_context, this);
        }
    }

    void ILogicalContextGuard.ThrowIfOtherLogicalContext() =>
        LifecycleNotifier.ThrowIfOtherContext((InterceptorSubjectContext)_context);

    Exception? ITopologyAdmissionCoordinator.CompleteStructuralWrite(
        InterceptorExecutor executor,
        StructuralWriteLease lease,
        Exception? primaryException)
    {
        bool runDeferredSweep;
        using (EnterGate())
        {
            executor.ReleaseStructuralWriteLease(lease);
            runDeferredSweep = _graph.HasDeferredSweep;
        }

        return runDeferredSweep ? TryDrainDeferredSweep(primaryException) : primaryException;
    }

    Exception? INonStructuralWriteCompletionCoordinator.CompleteNonStructuralWrite(Exception? primaryException)
    {
        if (!_graph.HasDeferredSweep)
        {
            return primaryException;
        }

        if (InterceptorExecutor.IsInsideLogicalCallback ||
            !InterceptorExecutor.IsCurrentLogicalContext((InterceptorSubjectContext)_context))
        {
            ThreadPool.UnsafeQueueUserWorkItem(
                static lifecycle => lifecycle.CompleteDeferredSweepInBackground(),
                this,
                preferLocal: false);
            return primaryException;
        }

        return TryDrainDeferredSweep(primaryException);
    }

    private void CompleteDeferredSweepInBackground()
    {
        try
        {
            CompleteDeferredSweep();
        }
        catch (Exception exception)
        {
            try
            {
                Trace.TraceError($"Completing a deferred lifecycle sweep failed: {exception}");
            }
            catch
            {
                // Background cleanup remains no-throw when diagnostics are misconfigured.
            }
        }
    }

    private Exception? TryDrainDeferredSweep(Exception? primaryException)
    {
        try
        {
            return DrainDeferredSweep(primaryException);
        }
        catch (LifecycleConflictException conflict) when (conflict.IsTransientCapture)
        {
            // The conflicting non-structural writer retries the sweep after leaving its terminal.
            return primaryException;
        }
        catch (LifecycleConflictException)
        {
            // A published journal still owns one of the affected subjects. Its final attachment
            // action retries the sweep after that subject becomes stable.
            return primaryException;
        }
    }

    private Exception? DrainDeferredSweep(Exception? primaryException)
    {
        using var capture = _notifier.BeginJournal();
        LifecycleNotifier.LifecycleJournal journal;
        using (EnterGate())
        {
            using var change = _graph.PrepareDeferredSweep(_notifier);
            journal = capture.Complete();
            if (change is not null)
            {
                _graph.Publish(change);
            }
        }

        return journal.Drain(primaryException);
    }

    OwnershipReservationToken ITopologyAdmissionCoordinator.AcquireOwnershipReservation(
        InterceptorExecutor executor, ReservationMode mode)
    {
        using (EnterGate())
        {
            return executor.TryAcquireOwnershipReservation(
                (InterceptorSubjectContext)_context, mode, this);
        }
    }

    void ITopologyAdmissionCoordinator.CompleteOwnershipReservation(
        InterceptorExecutor executor,
        OwnershipReservationToken token,
        bool retainCommittedOwnership)
    {
        bool runDeferredSweep;
        using (EnterGate())
        {
            if (!retainCommittedOwnership)
            {
                executor.TryGetAttachment(out var attachedContext, out var anchor, out _);
                retainCommittedOwnership = _graph.IsOwned(executor.Subject) ||
                    (anchor != SubjectAttachmentAnchorKind.None && ReferenceEquals(attachedContext, _context));
            }

            executor.ReleaseOwnershipReservation(token, detachIfLast: !retainCommittedOwnership);
            runDeferredSweep = _graph.HasDeferredSweep;
        }

        if (runDeferredSweep && TryDrainDeferredSweep(null) is { } exception)
        {
            try
            {
                Trace.TraceError($"Completing an ownership reservation failed: {exception}");
            }
            catch
            {
                // Reservation disposal and cleanup remain no-throw when diagnostics are misconfigured.
            }
        }
    }

    /// <inheritdoc />
    public bool TryAddProperties(SubjectPropertyRegistration registration)
    {
        LifecycleNotifier.ThrowIfOtherContext((InterceptorSubjectContext)_context);
        using var logicalContextScope = InterceptorExecutor.EnterLogicalContext((InterceptorSubjectContext)_context);
        var registrationExecutor = (InterceptorExecutor)registration.Subject.Executor;
        var attachment = registrationExecutor.AttachmentSnapshot;
        if (attachment.Phase == AttachmentPhase.Detaching &&
            !_graph.IsOwned(registration.Subject))
        {
            try
            {
                registration.PreparePublication(registrationExecutor);
            }
            catch (LifecycleConflictException conflict) when (conflict.IsTransientCapture)
            {
                return false;
            }

            return registration.TryPublishPrepared(registrationExecutor, attachment);
        }

        var reservations = LifecycleScratch.RentOwnershipReservations();
        try
        {
            while (true)
            {
                PropertyAdmission.Capture capture;
                try { capture = _admission.CaptureBatch(registration); }
                catch (LifecycleConflictException conflict) when (conflict.IsTransientCapture) { continue; }

                if (capture.AddedProperties.Count == 0)
                {
                    return true;
                }

                var rootExecutor = capture.Participants[0].Executor;

                bool reserved;
                try
                {
                    reserved = _graph.TryReserveParticipants(
                        capture.Participants, reservations,
                        exclusiveParticipants: true);
                }
                catch (LifecycleConflictException conflict) when (conflict.IsTransientCapture)
                {
                    continue;
                }

                if (!reserved)
                {
                    throw new InvalidOperationException(
                        "Another context claimed a subject of the admitted graph while this call was validating it.");
                }

                LifecycleNotifier.LifecycleJournal? journal = null;
                var retryCapture = false;
                using var journalCapture = _notifier.BeginJournal();
                using (EnterGate())
                {
                    var subject = registration.Subject;
                    if (!_graph.IsCaptureCurrent(capture.Participants))
                    {
                        retryCapture = true;
                    }
                    else if (!ReferenceEquals(rootExecutor.AttachedContext, _context) ||
                             !_graph.IsOwned(subject))
                    {
                        return false;
                    }
                    else
                    {
                        for (var index = 0; index < capture.Participants.Length; index++)
                        {
                            capture.Participants[index].Executor.PreflightPotentialAttachmentUpdate(
                                forceTransition: true);
                        }

                        journalCapture.PreflightCompletion(capture.ProjectionRevisionCapacity);
                    }
                }

                if (!retryCapture)
                {
                    registration.PublishReserved(
                        rootExecutor,
                        reservations[capture.Participants[0].Subject]);

                    using (EnterGate())
                    {
                        Debug.Assert(reservations.Values.All(reservation =>
                            reservation.IsActive((InterceptorSubjectContext)_context)));
                        Debug.Assert(ReferenceEquals(rootExecutor.AttachedContext, _context));
                        Debug.Assert(_graph.IsOwned(registration.Subject));

                        using var change = _admission.Prepare(capture, reservations);
                        journal = journalCapture.CompleteAfterPreflight();
                        _graph.Publish(change);
                    }
                }

                if (retryCapture)
                {
                    _graph.ReleaseUnusedReservations(reservations);
                    continue;
                }

                _graph.ReleaseUnusedReservations(reservations);
                if (journal!.Drain(null) is { } exception)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception).Throw();
                }

                return true;
            }
        }
        finally
        {
            _graph.ReleaseUnusedReservations(reservations);
            LifecycleScratch.Return(reservations);
        }
    }

    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
    }

    /// <inheritdoc />
    public void AttachSubjectToContext(IInterceptorSubject subject, IInterceptorSubjectContext context, SubjectAttachmentAnchorKind anchor)
    {
        LifecycleNotifier.ThrowIfTopologyChange((InterceptorSubjectContext)_context);

        if (!ReferenceEquals(context, _context))
        {
            throw new InvalidOperationException("The subject cannot be attached through the lifecycle of another context.");
        }

        if (anchor == SubjectAttachmentAnchorKind.None)
        {
            throw new InvalidOperationException("An attach without a root anchor would be released by the next reachability decision.");
        }

        using var logicalScope = InterceptorExecutor.EnterLogicalContext((InterceptorSubjectContext)_context);
        var executor = subject.Executor;
        lock (_gate)
        {
            var attachment = ((InterceptorExecutor)executor).AttachmentSnapshot;
            if (attachment.Phase != AttachmentPhase.Stable)
                throw LifecycleConflictException.Retryable(subject);

            InterceptorSubjectExtensions.ValidateRootAnchor(
                attachment.Context, attachment.Anchor, context, anchor);
            if (anchor == SubjectAttachmentAnchorKind.Provisional && attachment.Context is not null)
                return;
        }

        var visited = LifecycleScratch.RentSubjectSet();
        var reservations = LifecycleScratch.RentOwnershipReservations();
        var snapshots = new Dictionary<PropertyReference, StructuralSnapshot>(PropertyReference.Comparer);
        var subjectProperties = new Dictionary<IInterceptorSubject, ImmutableArray<SubjectPropertyMetadata>>(ReferenceEqualityComparer.Instance);
        try
        {
            while (true)
            {
                var participants = StructuralSnapshotBuilder.CaptureComponent(
                    subject, _context, _graph.State, visited, snapshots, subjectProperties);
                if (!_graph.TryReserveParticipants(participants, reservations, subject))
                {
                    throw new InvalidOperationException(
                        "Another context claimed a subject of this graph while the attach was validating it.");
                }

                LifecycleNotifier.LifecycleJournal? journal = null;
                var retryCapture = false;
                using (var journalCapture = _notifier.BeginJournal())
                {
                    using (EnterGate())
                    {
                        if (reservations.Values.Any(reservation =>
                                !reservation.IsActive((InterceptorSubjectContext)_context)))
                        {
                            throw LifecycleConflictException.Retryable(subject);
                        }

                        if (!_graph.IsCaptureCurrent(participants))
                        {
                            retryCapture = true;
                        }
                        else
                        {
                            executor.TryGetAttachment(out var attachedContext, out var currentAnchor, out _);
                            InterceptorSubjectExtensions.ValidateRootAnchor(
                                attachedContext, currentAnchor, context, anchor);
                            if (anchor == SubjectAttachmentAnchorKind.Provisional && attachedContext is not null)
                                return;

                            using var change = _graph.PrepareAttach(
                                subject, anchor, snapshots, subjectProperties, reservations, _notifier);
                            journal = journalCapture.Complete();
                            _graph.Publish(change);
                        }
                    }
                }

                if (retryCapture)
                {
                    ResetCaptureAttempt(reservations, snapshots, subjectProperties);
                    visited.Clear();
                    continue;
                }

                _graph.ReleaseUnusedReservations(reservations);
                if (journal!.Drain(null) is { } exception)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception).Throw();
                }

                return;
            }
        }
        finally
        {
            _graph.ReleaseUnusedReservations(reservations);
            LifecycleScratch.Return(reservations);
            LifecycleScratch.Return(visited);
        }
    }

    /// <inheritdoc />
    public void DetachSubjectFromContext(IInterceptorSubject subject, IInterceptorSubjectContext context)
    {
        LifecycleNotifier.ThrowIfTopologyChange((InterceptorSubjectContext)_context);

        if (!ReferenceEquals(context, _context))
        {
            throw new InvalidOperationException("The subject cannot be detached through the lifecycle of another context.");
        }

        using var logicalScope = InterceptorExecutor.EnterLogicalContext((InterceptorSubjectContext)_context);
        var executor = (InterceptorExecutor)subject.Executor;
        using var journalCapture = _notifier.BeginJournal();
        LifecycleNotifier.LifecycleJournal journal;
        using (EnterGate())
        {
            executor.TryGetAttachment(out var attachedContext, out var anchor, out _);
            InterceptorSubjectExtensions.ValidateDetach(attachedContext, anchor, context);
            using var change = _graph.PrepareDetach(subject, _notifier);
            journal = journalCapture.Complete();
            _graph.Publish(change);
        }

        if (journal.Drain(null) is { } exception)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }

    internal OwnershipGraph Graph => _graph;

    internal void CompleteAttachments(
        ImmutableArray<OwnershipGraph.AttachmentPlan> attachments)
    {
        foreach (var attachment in attachments)
        {
            lock (_gate)
            {
                if (_graph.IsOwned(attachment.Executor.Subject))
                {
                    attachment.Executor.FinalizeAttachment(
                        attachment.Context,
                        attachment.Revision);
                }
            }
        }
    }

    internal void CompleteDetachments(
        ImmutableArray<OwnershipGraph.DetachmentPlan> detachments)
    {
        foreach (var detachment in detachments)
        {
            var captureRevision = detachment.Executor.BeginFinalDetachmentCapture();
            try
            {
                lock (_gate)
                {
                    if (!_graph.IsOwned(detachment.Executor.Subject))
                    {
                        detachment.Executor.FinalizeDetachmentUnderCapture(
                            detachment.Context, detachment.Revision, captureRevision);
                    }
                }
            }
            finally
            {
                detachment.Executor.CompleteFinalDetachmentCapture(captureRevision);
            }
        }
    }

    internal void CompleteDeferredSweep()
    {
        if (_graph.HasDeferredSweep && TryDrainDeferredSweep(null) is { } exception)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }

    internal void FailNextJournalCompletionForTests(Exception failure) =>
        _notifier.FailNextJournalCompletionForTests(failure);

    /// <summary>Gets the committed incoming occurrence count, excluding a root anchor.</summary>
    public int GetReferenceCount(IInterceptorSubject subject)
    {
        return _graph.TryGetOwnership(subject)?.IncomingCount ?? 0;
    }

    /// <summary>Gets the subject's immutable occurrence-aware parent publication.</summary>
    public ImmutableArray<SubjectParent> GetParents(IInterceptorSubject subject)
    {
        return _graph.GetParents(subject);
    }

}
