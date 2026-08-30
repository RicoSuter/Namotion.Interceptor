using System.Collections.Immutable;
using System.Diagnostics;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Owns structural graph membership for one context: which subjects it holds, through which
/// occurrence-aware edges, and when a subject that lost its last support leaves.
/// </summary>
public sealed class LifecycleInterceptor : ILifecycleInterceptor, ILifecycleHandler,
    IWriteTerminalCoordinator, ITopologyAdmissionCoordinator
{
    private readonly IInterceptorSubjectContext _context;
    private readonly OwnershipGraph _graph;
    private readonly ReachabilityWalk _reachability;
    private readonly ReleaseTraversal _release;
    private readonly StructuralReconciler _reconciler;
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

    /// <summary>
    /// Creates the lifecycle for one context. That context is the single exact context this
    /// interceptor claims subjects for.
    /// </summary>
    public LifecycleInterceptor(IInterceptorSubjectContext context)
    {
        _context = context;
        _notifier = new LifecycleNotifier(context, this);
        _graph = new OwnershipGraph(context, this);
        _reachability = new ReachabilityWalk(_graph);
        _attach = new AttachTraversal(_notifier, _graph, _reachability);
        _release = new ReleaseTraversal(_notifier, _graph, _reachability);
        _reconciler = new StructuralReconciler(_notifier, _graph, _attach, _release);
        _admission = new PropertyAdmission(_graph, _reconciler, _attach);
    }

    #region Structural writes

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
        if (!ReferenceEquals(subject.Executor.AttachedContext, _context))
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
        var discovered = LifecycleScratch.RentSubjectList();
        var reservations = LifecycleScratch.RentOwnershipReservations();
        var seededSnapshots = new Dictionary<PropertyReference, StructuralSnapshot>(PropertyReference.Comparer);
        try
        {
            ReserveComponent(snapshot, discovered, reservations, seededSnapshots);
            using var journalCapture = _notifier.BeginJournal();
            var runWithheld = false;
            try
            {
                lock (context.Executor.SyncRoot)
                {
                    context.SetTerminalPredecessor(readValue(context.Property.Subject));
                    var gate = EnterGate();
                    try
                    {
                        if (!context.Executor.IsStructuralWriteLeaseActive(lease, (InterceptorSubjectContext)_context) ||
                            reservations.Values.Any(reservation =>
                                !((InterceptorExecutor)reservation.Subject.Executor).IsOwnershipReservationActive(
                                    reservation, (InterceptorSubjectContext)_context)))
                        {
                            throw LifecycleConflictException.Retryable(context.Property.Subject);
                        }

                        var change = _graph.PrepareWrite(
                            context.Property,
                            value,
                            snapshot,
                            context.Executor.Revision + 1,
                            seededSnapshots);
                        _graph.CommitReservations(reservations);
                        context.Executor.CommitRawWriteLocked(ref context, value, writeValue);
                        _reconciler.Publish(change, reservations);
                        context.CommittedLifecycleJournal = journalCapture.Complete(context.Property, context.Revision);
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
        }
        finally
        {
            _graph.ReleaseUnusedReservations(reservations);
            LifecycleScratch.Return(reservations);
            LifecycleScratch.Return(discovered);
        }
    }

    private void ReserveComponent(
        StructuralSnapshot snapshot,
        List<IInterceptorSubject> discovered,
        Dictionary<IInterceptorSubject, OwnershipReservationToken> reservations,
        Dictionary<PropertyReference, StructuralSnapshot> seededSnapshots)
    {
        var visited = LifecycleScratch.RentSubjectSet();
        try
        {
            _graph.DiscoverComponent(
                snapshot, visited, discovered, includeAttached: true, seededSnapshots: seededSnapshots);
        }
        finally
        {
            LifecycleScratch.Return(visited);
        }

        if (!_graph.TryReserveDiscovered(discovered, reservations))
        {
            throw new InvalidOperationException(
                "Another context claimed a subject of the assigned graph before its structural write reached the terminal.");
        }
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
        List<IInterceptorSubject>? deferredSweep;
        using (EnterGate())
        {
            executor.ReleaseStructuralWriteLease(lease);
            deferredSweep = _graph.PrepareDeferredSweep();
        }

        if (deferredSweep is null)
        {
            return primaryException;
        }

        using var capture = _notifier.BeginJournal();
        using (EnterGate())
        {
            foreach (var subject in deferredSweep)
            {
                var ownership = _graph.TryGetOwnership(subject);
                if (ownership is { IncomingCount: 0 } && !_graph.IsAnchored(subject) &&
                    !_graph.HasReservation(subject) && !OwnershipGraph.HasStructuralLease(subject))
                {
                    _release.ReleaseRoot(subject);
                }
            }
        }

        return capture.Complete(default, 0).Drain(primaryException);
    }

    OwnershipReservationToken ITopologyAdmissionCoordinator.AcquireOwnershipReservation(
        InterceptorExecutor executor,
        ReservationMode mode)
    {
        using (EnterGate())
        {
            return executor.TryAcquireOwnershipReservation((InterceptorSubjectContext)_context, mode, this);
        }
    }

    void ITopologyAdmissionCoordinator.CompleteOwnershipReservation(
        InterceptorExecutor executor,
        OwnershipReservationToken token,
        bool retainCommittedOwnership)
    {
        using (EnterGate())
        {
            if (!retainCommittedOwnership)
            {
                token.Subject.Executor.TryGetAttachment(out var attachedContext, out var anchor, out _);
                retainCommittedOwnership = _graph.IsOwned(token.Subject) ||
                    (anchor != SubjectAttachmentAnchorKind.None && ReferenceEquals(attachedContext, _context));
            }

            executor.ReleaseOwnershipReservation(token, detachIfLast: !retainCommittedOwnership);
        }
    }

    /// <inheritdoc />
    public bool TryAddProperties(SubjectPropertyRegistration registration)
    {
        using var logicalScope = InterceptorExecutor.EnterLogicalContext((InterceptorSubjectContext)_context);
        using (EnterGate())
        {
            var subject = registration.Subject;
            if (!ReferenceEquals(subject.Executor.AttachedContext, _context))
            {
                return false;
            }

            if (_graph.IsOwned(subject))
            {
                _admission.Admit(registration);
            }
            else
            {
                _admission.AdmitUnowned(registration);
            }

            return true;
        }
    }

    #endregion

    #region Ordered handler slot (the descent)

    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        HandleLifecycleChange(change, null);
    }

    internal void HandleLifecycleChange(
        SubjectLifecycleChange change,
        Dictionary<IInterceptorSubject, OwnershipReservationToken>? reservations)
    {
        if (change is { IsContextAttach: true, Property: not null })
        {
            _attach.SeedChildrenIfNeeded(change.Subject, reservations);
        }
    }

    #endregion

    #region Explicit attach and detach

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
        using (EnterGate())
        {
            var executor = subject.Executor;
            executor.TryGetAttachment(out var attachedContext, out var currentAnchor, out _);
            InterceptorSubjectExtensions.ValidateRootAnchor(attachedContext, currentAnchor, context, anchor);

            if (attachedContext is not null)
            {
                if (anchor != SubjectAttachmentAnchorKind.Provisional)
                {
                    _graph.SetAnchor(subject, anchor);
                }

                return;
            }

            var claimed = LifecycleScratch.RentSubjectList();
            var published = false;
            try
            {
                ClaimComponentForRoot(subject, anchor, claimed);
                SeedAndAttachComponent(subject);
                published = true;
            }
            finally
            {
                if (!published)
                {
                    RollbackRejectedAttach(subject, claimed);
                }

                LifecycleScratch.Return(claimed);
            }
        }
    }

    private void RollbackRejectedAttach(IInterceptorSubject subject, List<IInterceptorSubject> claimed)
    {
        var children = LifecycleScratch.RentChildList();
        try
        {
            _graph.CollectStructuralChildren(subject, children, seed: false);
            foreach (var (property, occurrence) in children)
            {
                _release.RemoveEdge(
                    occurrence.Subject,
                    property,
                    occurrence.SubjectOrdinal,
                    occurrence.Index);
            }

            _graph.SetAnchor(subject, SubjectAttachmentAnchorKind.None);

            foreach (var claimedSubject in claimed)
            {
                if (!_graph.IsOwned(claimedSubject))
                {
                    _graph.RemoveSnapshots(claimedSubject);
                }
            }

            _graph.ReleaseUnusedClaims(claimed);
        }
        catch (Exception exception)
        {
            Trace.TraceError(
                $"LifecycleInterceptor: rolling back a rejected attach of {subject.GetType().Name} " +
                $"failed with {exception.GetType().Name}: {exception.Message}. The attach's own " +
                "exception is propagating and this one is not, so part of the attach is still " +
                "published and the root is still attached; detach it explicitly to clean up.");
        }
        finally
        {
            LifecycleScratch.Return(children);
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
        using (EnterGate())
        {
            var executor = subject.Executor;
            executor.TryGetAttachment(out var attachedContext, out var anchor, out _);
            InterceptorSubjectExtensions.ValidateDetach(attachedContext, anchor, context);

            _graph.SetAnchor(subject, SubjectAttachmentAnchorKind.None);

            var ownership = _graph.TryGetOwnership(subject);
            if (ownership is null)
            {
                _graph.ReleaseClaim(subject);
                return;
            }

            if (ownership.IncomingCount == 0 || !_reachability.IsAnchorReachable(subject, null))
            {
                _release.ReleaseRoot(subject);
            }
        }
    }

    private void SeedAndAttachComponent(IInterceptorSubject subject)
    {
        _attach.SeedAndAttachChildren(subject);

        if (!_graph.IsOwned(subject))
        {
            _attach.AttachRoot(subject);
        }
    }

    private void ClaimComponentForRoot(IInterceptorSubject subject, SubjectAttachmentAnchorKind anchor, List<IInterceptorSubject> unattached)
    {
        var visited = LifecycleScratch.RentSubjectSet();
        try
        {
            _graph.DiscoverComponent(subject, visited, unattached);
            if (!_graph.TryClaimDiscovered(unattached, subject, anchor))
            {
                throw new InvalidOperationException(
                    "Another context claimed a subject of this graph while the attach was validating it.");
            }
        }
        finally
        {
            LifecycleScratch.Return(visited);
        }
    }

    #endregion

    #region Committed state queries

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

    /// <summary>
    /// Gets the number of committed incoming edge occurrences, which is the subject's reference
    /// count. An anchored root with no edge reports zero, so this is not an attachment predicate.
    /// </summary>
    /// <remarks>Takes no lock: consumers call it from inside lifecycle callbacks and their own locks.</remarks>
    public int GetReferenceCount(IInterceptorSubject subject)
    {
        return _graph.TryGetOwnership(subject)?.IncomingCount ?? 0;
    }

    /// <summary>
    /// Gets the subject's occurrence-aware parents. The first call on a subject activates parent
    /// publication for it; a subject nobody asks about never allocates a snapshot.
    /// </summary>
    /// <remarks>Takes no lock; see <see cref="OwnershipGraph.GetParents"/> for why that is required.</remarks>
    public ImmutableArray<SubjectParent> GetParents(IInterceptorSubject subject)
    {
        return _graph.GetParents(subject);
    }

    #endregion
}
