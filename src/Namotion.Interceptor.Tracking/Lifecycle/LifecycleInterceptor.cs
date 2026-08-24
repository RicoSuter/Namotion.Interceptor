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
/// </remarks>
public class LifecycleInterceptor : ILifecycleInterceptor
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
    // _attachmentLock note for the full order). Reentrancy is required: the structural write
    // protocol enters the gate in Core (through EnterStructuralWriteGate, before the chain is
    // resolved) and this interceptor enters it again from inside the chain, and an attach descent
    // re-enters it through ContextInheritanceHandler composing a child's context mid-callback.
    private readonly object _gate = new();

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
    /// At this point, the full object graph is still accessible.
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
        Monitor.Enter(_gate);
    }

    /// <inheritdoc />
    public void ExitStructuralWriteGate()
    {
        Monitor.Exit(_gate);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Scalar properties never take the topology lock. A structural property validates and claims the
    /// whole component the proposed value opens up before the backing writer runs, so a write that
    /// would pull in a subject of another context fails before the property changes.
    /// </remarks>
    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        var property = context.Property;
        var metadata = property.Metadata;
        if (!metadata.Type.CanContainSubjects<TProperty>() || metadata.IsDerived || !metadata.IsIntercepted)
        {
            // Scalar, derived or non-intercepted: never a graph edge. A derived value is a
            // projection of edges the stored properties already own, so reconciling it would
            // double-count every subject it exposes.
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

        lock (_gate)
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

                // The authoritative getter output rather than the proposed value: a normalizing or
                // derived setter may store a different graph than the caller passed.
                var getValue = metadata.GetValue;
                _reconciler.Reconcile(property, metadata, getValue is not null ? getValue(subject) : context.NewValue);
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

        if (!_graph.TryClaimDiscovered(claimed, null, SubjectAnchorKind.None))
        {
            claimed.Clear();
            throw new InvalidOperationException(
                "Another context claimed a subject of the assigned graph while this write was validating it. " +
                "The write was rejected before reaching the backing field.");
        }
    }

    /// <inheritdoc />
    public bool TryAddProperties(SubjectPropertyRegistrationContext registration)
    {
        // Reject a cross-context callback before the gate and before the input is enumerated: a
        // thread inside another lifecycle's callback holds that lifecycle's gate, so blocking on
        // this one can deadlock against opposing callbacks. Property lifecycle callbacks count
        // too: they are exempt from the structural write contract, not from the gate order. A
        // same-lifecycle callback already holds this gate reentrantly and is the supported
        // dynamic-property-initializer case.
        if (CallbackReentrancyGuard.IsInsideAnyCallback && !Monitor.IsEntered(_gate))
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

    #region Explicit attach and detach

    /// <inheritdoc />
    public void AttachSubjectToContext(IInterceptorSubject subject, IInterceptorSubjectContext context, SubjectAnchorKind anchor)
    {
        if (!ReferenceEquals(context, _context))
        {
            throw new InvalidOperationException("The subject cannot be attached through the lifecycle of another context.");
        }

        if (anchor == SubjectAnchorKind.None)
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
                if (anchor != SubjectAnchorKind.Provisional)
                {
                    _graph.SetAnchor(subject, anchor);
                }

                return;
            }

            ClaimComponentForRoot(subject, anchor);

            // Composing the context onto the executor is what makes the graph resolve its services,
            // and it is the transitional entry point that seeds and publishes the attach.
            if (!subject.Context.AddFallbackContext(_context))
            {
                OnContextComposed(subject);
            }
        }
    }

    /// <inheritdoc />
    public void DetachSubjectFromContext(IInterceptorSubject subject, IInterceptorSubjectContext context)
    {
        if (!ReferenceEquals(context, _context))
        {
            throw new InvalidOperationException("The subject cannot be detached through the lifecycle of another context.");
        }

        lock (_gate)
        {
            var executor = subject.Executor;
            executor.TryGetAttachment(out var attachedContext, out var anchor, out _);
            InterceptorSubjectExtensions.ValidateExplicitDetach(attachedContext, anchor, context);

            _graph.SetAnchor(subject, SubjectAnchorKind.None);

            var ownership = _graph.TryGetOwnership(subject);
            if (ownership is null)
            {
                _graph.ReleaseClaim(subject);
                subject.Context.RemoveFallbackContext(_context);
                return;
            }

            if (ownership.IncomingCount == 0 || !_reachability.IsAnchorReachable(subject, null))
            {
                _release.ReleaseRoot(subject);

                // Symmetric with the attach that composed it; a subject the edges still hold keeps
                // resolving the context it is still in.
                subject.Context.RemoveFallbackContext(_context);
            }
        }
    }

    /// <inheritdoc />
    public void OnContextComposed(IInterceptorSubject subject)
    {
        lock (_gate)
        {
            if (_graph.IsOwned(subject))
            {
                // The recursive descent: the subject already carries its incoming edge, so only its
                // own structural properties still have to be seeded and published.
                _attach.SeedChildrenIfNeeded(subject);
                return;
            }

            if (subject.Executor.AttachedContext is null)
            {
                ClaimComponentForRoot(subject, SubjectAnchorKind.Provisional);
            }
            else if (!ReferenceEquals(subject.Executor.AttachedContext, _context))
            {
                throw new InvalidOperationException(
                    "The subject is owned by a different context and cannot join this graph. Detach it from that context first.");
            }

            _attach.SeedAndAttachChildren(subject);

            // A back edge inside the seeded component can attach the subject before this point, in
            // which case it already published its context attach through that edge.
            if (!_graph.IsOwned(subject))
            {
                _attach.AttachRoot(subject);
            }
        }
    }

    /// <inheritdoc />
    public void OnContextDecomposed(IInterceptorSubject subject)
    {
        lock (_gate)
        {
            var ownership = _graph.TryGetOwnership(subject);
            if (ownership is null)
            {
                return;
            }

            // Decomposing the context that made the subject a provisional root gives that anchor up,
            // which is the inverse of what composing it did. An explicit anchor is only ever cleared
            // explicitly, and a subject an edge still holds stays.
            _graph.ClearProvisionalAnchor(subject);
            if (_graph.IsAnchored(subject) ||
                (ownership.IncomingCount > 0 && _reachability.IsAnchorReachable(subject, null)))
            {
                return;
            }

            _release.ReleaseRoot(subject);
        }
    }

    /// <summary>
    /// Validates the component the subject opens up and claims every unattached subject in it, with
    /// the requested anchor on the root. Nothing is published, so a rejection leaves no residue.
    /// </summary>
    private void ClaimComponentForRoot(IInterceptorSubject subject, SubjectAnchorKind anchor)
    {
        var visited = LifecycleScratch.RentSubjectSet();
        var unattached = LifecycleScratch.RentSubjectList();
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
            LifecycleScratch.Return(unattached);
        }
    }

    #endregion

    #region Committed state queries

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
