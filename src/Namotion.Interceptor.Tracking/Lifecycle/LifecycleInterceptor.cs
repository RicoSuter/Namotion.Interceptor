using System.Collections.Immutable;
using System.Diagnostics;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>Owns structural graph membership and lifecycle publication for one context.</summary>
public sealed class LifecycleInterceptor : ILifecycleInterceptor, ILifecycleHandler,
    IWriteTerminalCoordinator, ITopologyAdmissionCoordinator
{
    private readonly IInterceptorSubjectContext _context;
    private readonly OwnershipGraph _graph;
    private readonly ReleaseTraversal _release;
    private readonly AttachTraversal _attach;
    private readonly LifecycleNotifier _notifier;
    private readonly PropertyAdmission _admission;

    private readonly Lock _gate = new();

    [ThreadStatic]
    private static int _heldGateCount;

    private int _transactionsInFlight;

    private readonly Lock _withheldLock = new();
    private List<Action>? _withheldRecalculations;

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
        _attach = new AttachTraversal(_notifier, _graph);
        _release = new ReleaseTraversal(_notifier, _graph);
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
        if (_heldGateCount++ == 0)
        {
            Interlocked.Increment(ref _transactionsInFlight);
        }

        return new GateScope(this);
    }

    private bool ExitGate(bool runWithheld = true)
    {
        var leftTheTransaction = --_heldGateCount == 0;
        if (leftTheTransaction)
        {
            Interlocked.Decrement(ref _transactionsInFlight);
        }

        _gate.Exit();

        if (leftTheTransaction && runWithheld)
        {
            RunWithheldRecalculations();
        }

        return leftTheTransaction;
    }

    private readonly struct GateScope(LifecycleInterceptor lifecycle) : IDisposable
    {
        public void Dispose()
        {
            lifecycle.ExitGate();
        }

        internal bool ExitWithoutCallouts() => lifecycle.ExitGate(runWithheld: false);
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

        CallbackReentrancyGuard.ThrowIfInsideCallback();

        var subject = property.Subject;
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

        if (context.CommittedLifecycleJournal is LifecycleJournal journal &&
            _graph.GetSnapshot(journal.Property).SourceRevision == journal.Revision)
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
        var seededPropertyNames = new Dictionary<IInterceptorSubject, ImmutableArray<string>>(ReferenceEqualityComparer.Instance);
        try
        {
            while (true)
            {
                var participants = ReserveComponent(
                    snapshot, reservations, seededSnapshots, seededPropertyNames);
                var retryCapture = false;
                var runWithheld = false;
                try
                {
                    using (var journalCapture = _notifier.BeginJournal())
                    {
                        lock (context.Executor.SyncRoot)
                        {
                            context.SetTerminalPredecessor(readValue(context.Property.Subject));
                            var gate = EnterGate();
                            try
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
                                        seededPropertyNames,
                                        reservations,
                                        _notifier);
                                    if (change.RefreshCollection)
                                    {
                                        _notifier.RefreshCollectionProperty(context.Property, value);
                                    }

                                    var journal = journalCapture.Complete(
                                        context.Property,
                                        context.Executor.Revision + 1);
                                    context.Executor.CommitRawWriteLocked(ref context, value, writeValue);
                                    _graph.Publish(change);
                                    context.CommittedLifecycleJournal = journal;
                                }
                            }
                            finally
                            {
                                runWithheld = gate.ExitWithoutCallouts();
                            }
                        }
                    }
                }
                finally
                {
                    if (runWithheld)
                    {
                        RunWithheldRecalculations();
                    }
                }

                if (!retryCapture)
                {
                    break;
                }

                ResetCaptureAttempt(
                    reservations, seededSnapshots, seededPropertyNames);
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
        Dictionary<IInterceptorSubject, ImmutableArray<string>> seededPropertyNames)
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
                seededPropertyNames);

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
        Dictionary<IInterceptorSubject, ImmutableArray<string>> propertyNames)
    {
        _graph.ReleaseUnusedReservations(reservations);
        snapshots.Clear();
        propertyNames.Clear();
    }

    StructuralWriteLease ITopologyAdmissionCoordinator.AcquireStructuralWriteLease(InterceptorExecutor executor)
    {
        using (EnterGate())
        {
            return executor.TryAcquireStructuralWriteLease((InterceptorSubjectContext)_context, this);
        }
    }

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

        return runDeferredSweep ? DrainDeferredSweep(primaryException) : primaryException;
    }

    private Exception? DrainDeferredSweep(Exception? primaryException)
    {
        using var capture = _notifier.BeginJournal();
        using (EnterGate())
        {
            using var change = _graph.PrepareDeferredSweep(_notifier);
            if (change is not null)
            {
                _graph.Publish(change);
            }
        }

        return capture.Complete(default, 0).Drain(primaryException);
    }

    OwnershipReservationToken ITopologyAdmissionCoordinator.AcquireOwnershipReservation(
        InterceptorExecutor executor, ReservationMode mode,
        bool joinExclusive)
    {
        using (EnterGate())
        {
            return executor.TryAcquireOwnershipReservation(
                (InterceptorSubjectContext)_context, mode, this, joinExclusive);
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

        if (runDeferredSweep && DrainDeferredSweep(null) is { } exception)
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
        InterceptorExecutor.LogicalContextScope logicalScope;
        try
        {
            logicalScope = InterceptorExecutor.EnterLogicalContext((InterceptorSubjectContext)_context);
        }
        catch (InvalidOperationException exception)
        {
            throw new LifecycleContractViolationException(exception.Message);
        }

        using var logicalContextScope = logicalScope;
        var reservations = LifecycleScratch.RentOwnershipReservations();
        try
        {
            while (true)
            {
                PropertyAdmission.Capture capture;
                try { capture = _admission.CaptureBatch(registration); }
                catch (LifecycleConflictException) { continue; }
                if (!_graph.IsCaptureCurrent(capture.Participants))
                {
                    continue;
                }

                if (capture.AddedPropertyNames.IsEmpty)
                {
                    return true;
                }

                var rootExecutor = capture.Participants[0].Executor;

                if (!_graph.TryReserveParticipants(
                        capture.Participants, reservations, joinExclusiveRoot: registration.Subject))
                {
                    throw new InvalidOperationException(
                        "Another context claimed a subject of the admitted graph while this call was validating it.");
                }

                LifecycleJournal? journal = null;
                var retryCapture = false;
                var runWithheld = false;
                try
                {
                    using (var journalCapture = _notifier.BeginJournal())
                    {
                        var gate = EnterGate();
                        try
                        {
                            var subject = registration.Subject;
                            if (reservations.Values.Any(reservation =>
                                    !reservation.IsActive((InterceptorSubjectContext)_context)))
                            {
                                throw LifecycleConflictException.Retryable(subject);
                            }

                            if (!_graph.IsCaptureCurrent(capture.Participants))
                            {
                                retryCapture = true;
                            }
                            else if (!ReferenceEquals(rootExecutor.AttachedContext, _context))
                            {
                                return false;
                            }
                            else if (!_graph.IsOwned(subject))
                            {
                                if (registration.PublishPrepared(rootExecutor)) return true;
                                retryCapture = true;
                            }
                            else
                            {
                                using var change = _admission.Prepare(capture, reservations);
                                if (!registration.TryClaimPreparedPublication(rootExecutor))
                                {
                                    retryCapture = true;
                                }
                                else
                                {
                                    try
                                    {
                                        journal = journalCapture.Complete(default, 0);
                                        registration.PublishClaimed();
                                        registration.BeforeTopologyPublication?.Invoke();
                                        _graph.Publish(change);
                                    }
                                    finally
                                    {
                                        registration.ReleasePublicationClaim(rootExecutor);
                                    }
                                }
                            }
                        }
                        finally
                        {
                            runWithheld = gate.ExitWithoutCallouts();
                        }
                    }
                }
                finally
                {
                    if (runWithheld)
                    {
                        RunWithheldRecalculations();
                    }
                }

                if (retryCapture)
                {
                    _graph.ReleaseUnusedReservations(reservations);
                    continue;
                }

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
        CallbackReentrancyGuard.ThrowIfInsideCallback();

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
        executor.TryGetAttachment(out var initialContext, out var initialAnchor, out _);
        InterceptorSubjectExtensions.ValidateRootAnchor(initialContext, initialAnchor, context, anchor);
        if (initialContext is not null)
        {
            var anchorUpdated = false;
            using (EnterGate())
            {
                executor.TryGetAttachment(out var attachedContext, out var currentAnchor, out _);
                InterceptorSubjectExtensions.ValidateRootAnchor(attachedContext, currentAnchor, context, anchor);
                if (attachedContext is not null)
                {
                    if (anchor != SubjectAttachmentAnchorKind.Provisional)
                    {
                        using var change = _attach.Prepare(subject, anchor, [], [], []);
                        _graph.Publish(change);
                    }

                    anchorUpdated = true;
                }
            }

            if (anchorUpdated)
            {
                return;
            }
        }

        var visited = LifecycleScratch.RentSubjectSet();
        var reservations = LifecycleScratch.RentOwnershipReservations();
        var snapshots = new Dictionary<PropertyReference, StructuralSnapshot>(PropertyReference.Comparer);
        var propertyNames = new Dictionary<IInterceptorSubject, ImmutableArray<string>>(ReferenceEqualityComparer.Instance);
        try
        {
            while (true)
            {
                var participants = _attach.Capture(
                    subject, visited, snapshots, propertyNames);
                if (!_graph.TryReserveParticipants(participants, reservations, subject))
                {
                    throw new InvalidOperationException(
                        "Another context claimed a subject of this graph while the attach was validating it.");
                }

                LifecycleJournal? journal = null;
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
                            using var change = _attach.Prepare(
                                subject, anchor, snapshots, propertyNames, reservations);
                            journal = journalCapture.Complete(default, 0);
                            _graph.Publish(change);
                        }
                    }
                }

                if (retryCapture)
                {
                    ResetCaptureAttempt(reservations, snapshots, propertyNames);
                    visited.Clear();
                    continue;
                }

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
        CallbackReentrancyGuard.ThrowIfInsideCallback();

        if (!ReferenceEquals(context, _context))
        {
            throw new InvalidOperationException("The subject cannot be detached through the lifecycle of another context.");
        }

        using var logicalScope = InterceptorExecutor.EnterLogicalContext((InterceptorSubjectContext)_context);
        var executor = (InterceptorExecutor)subject.Executor;
        using var journalCapture = _notifier.BeginJournal();
        LifecycleJournal journal;
        using (EnterGate())
        {
            executor.TryGetAttachment(out var attachedContext, out var anchor, out _);
            InterceptorSubjectExtensions.ValidateDetach(attachedContext, anchor, context);
            using var change = _release.Prepare(subject, executor);
            journal = journalCapture.Complete(default, 0);
            _graph.Publish(change);
        }

        if (journal.Drain(null) is { } exception)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }

    internal OwnershipGraph Graph => _graph;

    internal bool TryRunWhenTransactionEnds(Action? recalculation)
    {
        lock (_withheldLock)
        {
            if (Volatile.Read(ref _transactionsInFlight) <= (_gate.IsHeldByCurrentThread ? 1 : 0))
            {
                return false;
            }

            if (recalculation is not null)
            {
                (_withheldRecalculations ??= []).Add(recalculation);
            }

            return true;
        }
    }

    private void RunWithheldRecalculations()
    {
        List<Action>? withheld;
        lock (_withheldLock)
        {
            withheld = _withheldRecalculations;
            _withheldRecalculations = null;
        }

        if (withheld is null)
        {
            return;
        }

        foreach (var recalculation in withheld)
        {
            try
            {
                recalculation();
            }
            catch (Exception exception)
            {
                Trace.TraceError(
                    "LifecycleInterceptor: a recalculation deferred until this topology transaction " +
                    $"ended failed with {exception.GetType().Name}: {exception.Message}");
            }
        }
    }

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
