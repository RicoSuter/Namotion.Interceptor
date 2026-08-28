using System.Collections.Immutable;
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

    // One reentrant topology lock per lifecycle, which is one per context because the lifecycle is a
    // singleton contract, and the outermost lock of the structural write order (see the executor's
    // _attachmentLock note for the full order). Only this interceptor enters it: for a structural
    // write from inside the chain, where it is the last interceptor by the chain partition, so no
    // registered interceptor ever runs while it is held. Reentrancy is required because a
    // same-lifecycle callback re-enters it through TryAddProperties (the
    // dynamic-property-initializer case).
    private readonly Lock _gate = new();

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
    /// The subject's ownership record and baselines are already gone by this point, so GetParents()
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
    /// <remarks>
    /// Scalar properties never take the topology lock; the classification is the declared property
    /// type, so a write whose compile-time type is narrower than a structural declared type still
    /// runs the full section. A structural property validates and claims the whole component the
    /// proposed value opens up before the backing writer runs, so a write that would pull in a
    /// subject of another context fails before the property changes.
    /// </remarks>
    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        var property = context.Property;
        var metadata = property.Metadata;
        if (!metadata.CanContainSubjects || metadata.IsDerived || !metadata.IsIntercepted)
        {
            // Scalar, derived or non-intercepted: never a graph edge. [Derived] declares the
            // value to be a function of other state, which makes the property a cache rather
            // than the store of record, whether or not a backing field holds the result.
            next(ref context);
            return;
        }

        CallbackReentrancyGuard.ThrowIfInsideCallback();

        var subject = property.Subject;
        var attachedContext = subject.Executor.AttachedContext;
        if (ReferenceEquals(attachedContext, _context))
        {
            lock (_gate)
            {
                // Re-check under the gate: every lifecycle-mediated transition of this context's
                // subjects holds the gate, so inside it the arm is stable against them. A raw SPI
                // transition can still land, which the terminal's commit predicate answers.
                attachedContext = subject.Executor.AttachedContext;
                if (ReferenceEquals(attachedContext, _context))
                {
                    WriteOwnedProperty(ref context, next, property, metadata, subject);
                    return;
                }

                if (attachedContext is not null)
                {
                    context.AttachmentMoved = true;
                    return;
                }
            }

            // Released while this write waited for the gate: fall through to the write-through
            // arm, outside the gate.
        }
        else if (attachedContext is not null)
        {
            // The chain belongs to a context the subject has left; the executor's retry runs the
            // current owner's chain and protocol.
            context.AttachmentMoved = true;
            return;
        }

        // The write-through arm: the subject is unattached, either because this thread's own
        // upstream interceptor released it or because a cross-thread transition landed. No claims
        // and no reconcile, matching the reconcile-entry semantics for a released parent, but the
        // terminal still commits under the null rule so notification and derived recalculation
        // fire on this same chain. Expected must be nulled first: if it stayed this context, a
        // cross-thread re-attach before the commit would satisfy the predicate and land a value
        // the re-attach seeding already read past.
        context.ExpectedAttachedContext = null;
        next(ref context);
    }

    /// <summary>
    /// The gate section of a structural write on a subject this context owns. Runs under
    /// <see cref="_gate"/>.
    /// </summary>
    private void WriteOwnedProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next, PropertyReference property, in SubjectPropertyMetadata metadata, IInterceptorSubject subject)
    {
        if (!_graph.IsOwned(subject))
        {
            next(ref context);
            return;
        }

        var claimed = LifecycleScratch.RentSubjectList();
        try
        {
            ClaimProposedComponent(metadata.Type, context.NewValue, claimed);

            next(ref context);

            if (context is { IsWritten: true, AttachmentMoved: false })
            {
                // The authoritative getter output rather than the proposed value: a normalizing or
                // derived setter may store a different graph than the caller passed. Skipped when
                // the terminal aborted: an aborted attempt committed nothing to reconcile, and the
                // executor re-routes the whole write.
                var getValue = metadata.GetValue;
                _reconciler.Reconcile(property, metadata, getValue is not null ? getValue(subject) : context.NewValue);
            }
        }
        finally
        {
            // Claims that never became ownership are handed back, aborted attempts included; see
            // OwnershipGraph.ReleaseUnusedClaims for what leaves them behind.
            _graph.ReleaseUnusedClaims(claimed);
            LifecycleScratch.Return(claimed);
        }
    }

    /// <summary>
    /// Validates every subject the proposed value reaches against this context and claims the
    /// unattached ones, before the backing writer runs. The visited set stays alive across the
    /// claim so <see cref="OwnershipGraph.TryClaimDiscovered"/> can verify the claimed subjects'
    /// getters against it; returning it earlier would clear it (the pool clears on return).
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

            if (!_graph.TryClaimDiscovered(claimed, visited, null, SubjectAttachmentAnchorKind.None))
            {
                claimed.Clear();
                throw new InvalidOperationException(
                    "Another context claimed a subject of the assigned graph while this write was validating it. " +
                    "The write was rejected before reaching the backing field.");
            }
        }
        finally
        {
            LifecycleScratch.Return(visited);
        }
    }

    /// <inheritdoc />
    public bool TryAddProperties(SubjectPropertyRegistration registration)
    {
        // Reject a cross-context callback before the gate and before the input is enumerated: a
        // thread inside another lifecycle's callback holds that lifecycle's gate, so blocking on
        // this one can deadlock against opposing callbacks. Property lifecycle callbacks count
        // too: they are published under the same gate, so the deadlock shape is identical. A
        // same-lifecycle callback already holds this gate reentrantly and is the supported
        // dynamic-property-initializer case.
        if (CallbackReentrancyGuard.IsInsideAnyCallback && !_gate.IsHeldByCurrentThread)
        {
            throw new InvalidOperationException(
                "AddProperties on a subject owned by another context is not supported from a " +
                "lifecycle callback, because blocking on a second lifecycle gate there can " +
                "deadlock against opposing callbacks. The input was not enumerated and nothing " +
                "was published.");
        }

        lock (_gate)
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
                // Claimed for this context but not yet published into the graph, which is only
                // observable from inside this thread's own attach descent; see AdmitUnowned for
                // the two shapes.
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

        lock (_gate)
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

            ClaimComponentForRoot(subject, anchor);
            SeedAndAttachComponent(subject);
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

        lock (_gate)
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
    /// the requested anchor on the root. Nothing is published, so a rejection leaves no residue.
    /// </summary>
    private void ClaimComponentForRoot(IInterceptorSubject subject, SubjectAttachmentAnchorKind anchor)
    {
        var visited = LifecycleScratch.RentSubjectSet();
        var unattached = LifecycleScratch.RentSubjectList();
        try
        {
            _graph.DiscoverComponent(subject, visited, unattached);
            if (!_graph.TryClaimDiscovered(unattached, visited, subject, anchor))
            {
                throw new InvalidOperationException(
                    "Another context claimed a subject of this graph while the attach was validating it.");
            }
        }
        finally
        {
            LifecycleScratch.Return(visited);
            LifecycleScratch.Return(unattached);
        }
    }

    #endregion

    #region Committed state queries

    // Internal for tests only: committed baselines have no public observer, and the
    // released-parent regression tests must assert that none survives a subject's release.
    internal OwnershipGraph Graph => _graph;

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
