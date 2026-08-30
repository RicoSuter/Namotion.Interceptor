using System.Collections.Immutable;
using System.Diagnostics;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Owns structural graph membership for one context: which subjects it holds, through which
/// occurrence-aware edges, and when a subject that lost its last support leaves.
/// </summary>
/// <remarks>
/// A subject is attached to exactly one context. It is held either by a root anchor (an explicit
/// attach, or the provisional anchor a context-taking constructor leaves) or by a path of structural
/// edges from an anchored root. The provisional anchor is consumed by the first edge that supports
/// the subject independently of that anchor, so construction-time attachment does not create roots
/// that nothing ever releases; an explicit anchor is only ever cleared explicitly.
///
/// All topology changes are serialized by one private reentrant lock. Parent and reference-count
/// reads deliberately do not take it: they read published per-subject state, because consumers call
/// them from inside their own locks and from inside lifecycle callbacks.
///
/// Sealed because both the ordering seam and the default-lifecycle idempotence check key on this
/// exact type: a subclass would silently unbind every [RunsBefore]/[RunsAfter] constraint naming
/// <see cref="LifecycleInterceptor"/> and would satisfy the WithLifecycle() exists check without
/// being the default lifecycle. Third parties extend through <see cref="ILifecycleInterceptor"/>.
/// </remarks>
public sealed class LifecycleInterceptor : ILifecycleInterceptor, ILifecycleHandler
{
    private readonly IInterceptorSubjectContext _context;
    private readonly OwnershipGraph _graph;
    private readonly ReachabilityWalk _reachability;
    private readonly ReleaseTraversal _release;
    private readonly StructuralReconciler _reconciler;
    private readonly AttachTraversal _attach;
    private readonly LifecycleNotifier _notifier;
    private readonly PropertyAdmission _admission;

    // One reentrant topology lock per lifecycle, and the outermost lock of the structural write
    // order (see the executor's _attachmentLock note for the full order). Reentrancy is required:
    // Core enters the gate before the chain is resolved and this interceptor enters it again from
    // inside the chain. Always taken through EnterGate, never directly, so the
    // one-transaction-per-thread rule below sees every acquisition.
    private readonly Lock _gate = new();

    // How many topology gates the current thread holds, across every lifecycle. Gates have no
    // order among themselves, so a thread holding one and blocking on another deadlocks against a
    // thread taking them the other way round: a second transaction on a different lifecycle is
    // rejected instead of waiting.
    [ThreadStatic]
    private static int _heldGateCount;

    // How many threads are inside a topology transaction of this lifecycle. Counted once per
    // thread, not once per acquisition, because the gate is reentrant. The window between a
    // terminal store and its reconcile is user-visible and cannot be closed, so readers that would
    // otherwise convict a subject caught in it ask this instead.
    private int _transactionsInFlight;

    // Work registered by a reader that withheld a verdict because this lifecycle was mid-transaction,
    // to be re-run once it is not. A handshake rather than an inference: nothing else guarantees
    // that the thread which opened the window will recalculate what read through it. Guarded by its
    // own lock, which is a leaf: it is taken by a thread holding the reader's own lock, and
    // released before anything registered here runs.
    private readonly Lock _withheldLock = new();
    private List<Action>? _withheldRecalculations;

    /// <summary>
    /// Raised when a subject is attached to the object graph.
    /// Handlers must be exception-free and fast (invoked inside lock).
    /// </summary>
    public event Action<SubjectLifecycleChange>? SubjectAttached
    {
        add => _notifier.SubjectAttached += value;
        remove => _notifier.SubjectAttached -= value;
    }

    /// <summary>
    /// Raised when a subject is about to be detached from the object graph.
    /// Fires BEFORE ILifecycleHandler.HandleLifecycleChange (symmetric with SubjectAttached which fires AFTER).
    /// The subject's ownership record and snapshots are already gone by this point, so GetParents()
    /// answers empty and GetReferenceCount() answers zero; the subject still resolves its context,
    /// which is what the teardown callbacks need.
    /// Handlers must be exception-free and fast (invoked inside lock).
    /// </summary>
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
        _notifier = new LifecycleNotifier(context);
        _graph = new OwnershipGraph(context);
        _reachability = new ReachabilityWalk(_graph);
        _attach = new AttachTraversal(_notifier, _graph, _reachability);
        _release = new ReleaseTraversal(_notifier, _graph, _reachability);
        _reconciler = new StructuralReconciler(_notifier, _graph, _attach, _release);
        _admission = new PropertyAdmission(_graph, _reconciler, _attach);
    }

    #region Structural writes

    /// <inheritdoc />
    public void EnterStructuralWriteGate()
    {
        EnterGate();
    }

    /// <inheritdoc />
    public void ExitStructuralWriteGate()
    {
        ExitGate();
    }

    /// <summary>
    /// Enters the topology gate, rejecting a second transaction on a different lifecycle before it
    /// can block. Re-entering the gate this thread already holds is legal and load-bearing.
    /// </summary>
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
            // Past the rejection above, a nonzero count means this thread already holds this very
            // gate, so a reentrant acquisition is not a new transaction.
            Interlocked.Increment(ref _transactionsInFlight);
        }

        return new GateScope(this);
    }

    private void ExitGate()
    {
        // Decrement first, so an unbalanced exit leaves the count too low rather than too high: a
        // count stranded above zero on a pooled thread would reject that thread's next unrelated
        // transaction, while one below zero only stops the rule firing.
        var leftTheTransaction = --_heldGateCount == 0;
        if (leftTheTransaction)
        {
            // Before the gate is released and before the drain below takes the registration lock,
            // so a reader that registers after this point reads a settled count and is told to
            // decide for itself rather than to wait for a transaction that has ended.
            Interlocked.Decrement(ref _transactionsInFlight);
        }

        _gate.Exit();

        if (leftTheTransaction)
        {
            // Unconditionally, without first peeking at the list: a peek outside the registration
            // lock creates a third outcome for a registration that has passed the count check and
            // not yet published its entry, which is then neither drained nor refused.
            RunWithheldRecalculations();
        }
    }

    /// <summary>Releases what <see cref="EnterGate"/> took. A struct, so the using costs nothing.</summary>
    private readonly struct GateScope(LifecycleInterceptor lifecycle) : IDisposable
    {
        public void Dispose()
        {
            lifecycle.ExitGate();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Scalar properties never take the topology lock. A structural property validates and claims the
    /// whole component the proposed value opens up before the backing writer runs, so a write that
    /// would pull in a subject of another context fails before the property changes. The value the
    /// terminal actually stored is claimed as well, because a normalizing or hand-written terminal
    /// can store a graph the caller never proposed.
    ///
    /// A terminal that stores a subject the write never proposed is a contract violation, and the
    /// guarantee has one boundary there: the graph is left untouched, but the backing field holds
    /// whatever that terminal stored. The framework can only invoke the terminal it was given, and a
    /// terminal that is not a function of its argument cannot be replayed to restore the prior
    /// value: replaying it with the pre-write value re-stores the same subject and fails again, and
    /// going through <c>SetValue</c> is worse, being full chain re-entry with the same substitution
    /// and therefore unbounded recursion. A terminal that stores what it was given, which is every
    /// terminal the source generator emits, never reaches the boundary and skips the second claim.
    /// </remarks>
    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        var property = context.Property;
        var metadata = property.Metadata;
        if (!metadata.Type.CanContainSubjects<TProperty>() || !metadata.IsIntercepted ||
            metadata is { IsDerived: true, IsDynamic: true, SetValue: null })
        {
            // Scalar, non-intercepted or a derived projection: never a graph edge. Same rule as
            // OwnershipGraph.IsStructural, which carries the reasoning, restated here because the
            // generic overload of CanContainSubjects is the write path's type fast path.
            next(ref context);
            return;
        }

        CallbackReentrancyGuard.ThrowIfInsideCallback();

        var subject = property.Subject;
        if (!ReferenceEquals(subject.Executor.AttachedContext, _context))
        {
            // Not this lifecycle's subject: either unattached, or owned by another context whose own
            // lifecycle reconciles this write.
            next(ref context);
            return;
        }

        using (EnterGate())
        {
            if (!_graph.IsOwned(subject))
            {
                // Claimed for this context but not published: a root whose own structural getter
                // writes back while the explicit attach seeds it at callback depth zero, or a
                // subject between losing its ownership record and having its claim handed back. The
                // reconcile would find no owner to publish edges for, and the seed that follows
                // reads the committed value anyway.
                next(ref context);
                return;
            }

            var claimed = LifecycleScratch.RentSubjectList();
            try
            {
                ClaimProposedComponent(metadata.Type, context.NewValue, claimed);

                next(ref context);

                // The authoritative getter output rather than the proposed value: a normalizing or
                // derived setter may store a different graph than the caller passed.
                var getValue = metadata.GetValue;
                var storedValue = getValue is not null ? getValue(subject) : context.NewValue;
                if (!IsTheProposedValue(storedValue, context.NewValue))
                {
                    // The terminal stored something else, so the claim above covers a graph that is
                    // not the one now in the property. Claiming what was actually stored keeps the
                    // foreign-subject rejection ahead of every graph mutation: the snapshot, the
                    // ownership records and the attach notifications all come after this point.
                    ClaimProposedComponent(metadata.Type, storedValue, claimed);
                }

                _reconciler.Reconcile(property, metadata, storedValue, context.Revision);
            }
            finally
            {
                // Claims that never became ownership are handed back; see
                // OwnershipGraph.ReleaseUnusedClaims for what leaves them behind.
                _graph.ReleaseUnusedClaims(claimed);
                LifecycleScratch.Return(claimed);
            }
        }
    }

    /// <summary>
    /// Whether the terminal stored the value it was given, which is what lets a write skip claiming
    /// the stored component a second time.
    /// </summary>
    /// <remarks>
    /// The question is always identity of storage, never equality of value. A reference-typed value
    /// is compared by reference and deliberately not through <see cref="object.Equals(object?)"/>:
    /// a type that overrides equality could otherwise report a different instance as the same value
    /// and suppress the claim on subjects nothing validated. A value type has no reference to
    /// compare, because the authoritative getter boxes it afresh on every call, so the question can
    /// only be asked of one whose own equality is itself storage identity, which
    /// <see cref="ImmutableArray{T}"/> is. Every other value type is claimed a second time instead,
    /// which costs one scan and cannot be wrong.
    /// </remarks>
    private static bool IsTheProposedValue<TProperty>(object? storedValue, TProperty proposedValue)
    {
        if (default(TProperty) is null)
        {
            return ReferenceEquals(storedValue, proposedValue);
        }

        return StorageIdentity<TProperty>.IsItsOwnEquality &&
               storedValue is TProperty typedStoredValue &&
               EqualityComparer<TProperty>.Default.Equals(typedStoredValue, proposedValue);
    }

    /// <summary>
    /// Whether a value type's own equality is a comparison of its storage rather than of its
    /// contents. Resolved once per property type by the runtime, so the write path reads a static.
    /// </summary>
    private static class StorageIdentity<TProperty>
    {
        internal static readonly bool IsItsOwnEquality =
            typeof(TProperty).IsGenericType &&
            typeof(TProperty).GetGenericTypeDefinition() == typeof(ImmutableArray<>);
    }

    /// <summary>
    /// Validates every subject the proposed value reaches against this context and claims the
    /// unattached ones, before the backing writer runs.
    /// </summary>
    private void ClaimProposedComponent(Type declaredType, object? proposedValue, List<IInterceptorSubject> claimed)
    {
        if (proposedValue is null)
        {
            return;
        }

        var visited = LifecycleScratch.RentSubjectSet();
        try
        {
            _graph.DiscoverComponent(declaredType, proposedValue, visited, claimed);
        }
        finally
        {
            LifecycleScratch.Return(visited);
        }

        if (!_graph.TryClaimDiscovered(claimed, null, SubjectAttachmentAnchorKind.None))
        {
            claimed.Clear();
            throw new InvalidOperationException(
                "Another context claimed a subject of the assigned graph while this write was validating it. " +
                "The write was rejected before reaching the backing field.");
        }
    }

    /// <inheritdoc />
    public bool TryAddProperties(SubjectPropertyRegistration registration)
    {
        // EnterGate rejects an admission that would open a second transaction, before the input is
        // enumerated and before anything blocks. A same-lifecycle callback re-enters this gate and
        // is the supported dynamic-property-initializer case.
        using (EnterGate())
        {
            var subject = registration.Subject;
            if (!ReferenceEquals(subject.Executor.AttachedContext, _context))
            {
                // The attachment moved between the caller's routing read and the gate; the caller
                // re-routes against the fresh attachment.
                return false;
            }

            if (_graph.IsOwned(subject))
            {
                _admission.Admit(registration);
            }
            else
            {
                // Claimed for this context but not owned by the graph: this thread's own attach
                // descent before it publishes, or a detach callback after the release dropped the
                // record; see AdmitUnowned for the shapes.
                _admission.AdmitUnowned(registration);
            }

            return true;
        }
    }

    #endregion

    #region Ordered handler slot (the descent)

    /// <summary>
    /// The lifecycle's slot in the ordered <see cref="ILifecycleHandler"/> fan-out: when an edge
    /// pulls a subject into the graph, it seeds that subject's own structural properties, which is
    /// the recursive attach descent. This slot is the public ordering seam: a handler runs ahead
    /// of the descent with <c>[RunsBefore(typeof(LifecycleInterceptor))]</c> and behind it with
    /// <c>[RunsAfter]</c>, and detach changes pass through it unhandled so that the same seam
    /// orders both directions.
    /// </summary>
    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        if (change is { IsContextAttach: true, Property: not null })
        {
            _attach.SeedChildrenIfNeeded(change.Subject);
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

        using (EnterGate())
        {
            var executor = subject.Executor;
            executor.TryGetAttachment(out var attachedContext, out var currentAnchor, out _);
            InterceptorSubjectExtensions.ValidateRootAnchor(attachedContext, currentAnchor, context, anchor);

            if (attachedContext is not null)
            {
                // Already in this context: promote the anchor without repeating attach callbacks. A
                // provisional request never promotes, it is only a construction-time default.
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

    /// <summary>
    /// Hands back everything a rejected attach had already written. Discovery reads user values
    /// before the claim publishes anything, so a concurrent write can install a child that seeding
    /// then refuses, and the anchor, the seeded snapshots and the claims taken in between must not
    /// outlive that refusal.
    /// </summary>
    /// <remarks>
    /// Whatever the seed managed to publish hangs off the root's committed snapshots, so removing
    /// those edges releases it the ordinary way, cascade and detach callbacks included. That is also
    /// the only handle on a subject a concurrent write installed after the scan: it is published but
    /// was never in the claimed set, so a claim-only rollback would leave it attached.
    ///
    /// The order is deliberate: the root keeps its anchor, its snapshots and its claim until the
    /// drain has actually finished, so a rollback that cannot complete leaves the root attached and
    /// detachable rather than stripped of the very state <c>DetachFromContext</c> needs.
    /// </remarks>
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
            // This runs while the attach's own exception is in flight, and that one is the
            // diagnostic worth keeping: it says why the attach was refused, where this one only
            // says the cleanup after it went wrong. So the original wins and this is traced instead
            // of thrown, the rollback stops where it stood, and the root keeps the anchor and claim
            // an explicit detach needs.
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

    /// <summary>
    /// Seeds and publishes a freshly claimed root's component. Runs under <see cref="_gate"/>.
    /// </summary>
    private void SeedAndAttachComponent(IInterceptorSubject subject)
    {
        _attach.SeedAndAttachChildren(subject);

        // A back edge inside the seeded component can attach the subject before this point, in
        // which case it already published its context attach through that edge.
        if (!_graph.IsOwned(subject))
        {
            _attach.AttachRoot(subject);
        }
    }

    /// <summary>
    /// Validates the component the subject opens up and claims every unattached subject in it, with
    /// the requested anchor on the root. The claims and the anchor are the only things it writes,
    /// and <see cref="RollbackRejectedAttach"/> is what hands them back when the attach is refused
    /// after this point.
    /// </summary>
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

    // Internal for tests only: committed snapshots have no public observer, and the
    // released-parent regression tests must assert that none survives a subject's release.
    internal OwnershipGraph Graph => _graph;

    /// <summary>
    /// Asks whether there is an in-flight transaction to wait for, and registers work to run once it
    /// ends. Returns false when there is nothing in flight, in which case nothing was registered and
    /// the caller decides on the value it is holding. A null <paramref name="recalculation"/> asks
    /// the question without registering, for a caller whose earlier booking is still outstanding.
    /// </summary>
    /// <remarks>
    /// The question and the registration share one lock, and the transaction count is decremented
    /// before the drain takes that lock, so the two cannot both miss: a registration that lands
    /// before the drain's swap is drained, and one that lands after it reads a count of zero and is
    /// refused. That is also why every caller has to ask here rather than answering from state of
    /// its own. Nothing registered here runs under this lock, and the caller may hold its own lock
    /// while registering, so this stays a leaf.
    /// </remarks>
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

    /// <summary>
    /// Runs everything a reader deferred until this transaction ended. The gate is already released,
    /// so the work is free to take it again, and this lifecycle holds no lock while it runs.
    /// </summary>
    /// <remarks>
    /// Exceptions do not escape: this is reached from a <c>finally</c>, so a conviction that
    /// surfaces here would replace the exception of a transaction that is already failing, and the
    /// reader that produced the value has long returned. It is traced instead, and raised against a
    /// caller on the next evaluation with nothing in flight. Nothing schedules that evaluation, so
    /// this is best effort and not a guarantee: a derived property whose dependencies are written
    /// once at startup, or one orphaned by the last write of a batch, is reported only into the
    /// trace, which is silent unless a listener is configured.
    /// </remarks>
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
