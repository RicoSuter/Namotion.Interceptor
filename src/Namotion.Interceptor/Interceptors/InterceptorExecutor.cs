using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Interceptors;

public sealed class InterceptorExecutor : InterceptorSubjectContext, IInterceptorExecutor
{
    private readonly IInterceptorSubject _subject;

    public InterceptorExecutor(IInterceptorSubject subject)
    {
        _subject = subject;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TProperty GetPropertyValue<TProperty>(string propertyName, Func<IInterceptorSubject, TProperty> readValue)
    {
        var context = new PropertyReadContext<TProperty>(new PropertyReference(_subject, propertyName));
        return ExecuteInterceptedRead(ref context, readValue);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool SetPropertyValue<TProperty>(string propertyName, TProperty newValue, TProperty currentValue, Action<IInterceptorSubject, TProperty> writeValue)
    {
        var context = new PropertyWriteContext<TProperty>(
            new PropertyReference(_subject, propertyName),
            currentValue,
            newValue);

        ExecuteInterceptedWrite(ref context, writeValue);
        return context.IsWritten;
    }

    /// <summary>
    /// Cascade re-entry path: skips the lazy-resolve machinery by pre-populating the new write
    /// context's timestamp cache. Lets the cascade share the trigger's captured time without
    /// pushing a <see cref="SubjectChangeContext.WithChangedTimestamp(DateTimeOffset?)"/> scope.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool SetPropertyValue<TProperty>(string propertyName, TProperty newValue, TProperty currentValue, Action<IInterceptorSubject, TProperty> writeValue, long rawTimestamp)
    {
        var context = new PropertyWriteContext<TProperty>(
            new PropertyReference(_subject, propertyName),
            currentValue,
            newValue,
            rawTimestamp);

        ExecuteInterceptedWrite(ref context, writeValue);
        return context.IsWritten;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public object? InvokeMethod(string methodName, object?[] parameters, Func<IInterceptorSubject, object?[], object?> invokeMethod)
    {
        var context = new MethodInvocationContext(_subject, methodName, parameters);
        return ExecuteInterceptedInvoke(ref context, invokeMethod);
    }

    // Per-subject lifecycle position, all written under the base's _mutationLock. On the executor
    // rather than in subject.Data because each Data entry costs a ConcurrentDictionary node of
    // roughly 50 to 60 bytes per attached subject, and because the guard that reads _attachContext
    // lives in AddFallbackContext, a method on the context, so a subject-side record would mean a
    // cross-object lookup on a path that is otherwise a field read. Not on the base class because
    // both guards are executor-only semantics: on a plain context, adding a lifecycle-bearing
    // context is exactly how services are composed and must not throw.
    private IInterceptorSubjectContext? _attachContext;
    private ILifecycleInterceptor? _owner;
    private ImmutableArray<ILifecycleInterceptor> _attachInterceptors = ImmutableArray<ILifecycleInterceptor>.Empty;
    private int _referenceCount;

    internal IInterceptorSubjectContext? AttachContext => Volatile.Read(ref _attachContext);

    /// <summary>
    /// Neither half is sufficient alone: a property-attached child has no record but is owned, and
    /// a subject root-attached through a core-only custom ILifecycleInterceptor has a record but no
    /// owner, because only Tracking's LifecycleInterceptor claims ownership.
    /// </summary>
    internal bool IsAttachedCore => Volatile.Read(ref _owner) is not null || Volatile.Read(ref _attachContext) is not null;

    /// <summary>
    /// A snapshot, as the public GetReferenceCount() has always been. Volatile because a plain
    /// field does not supply the ordering the ConcurrentDictionary this replaced supplied for free.
    /// The detach guard deliberately does not go through here: it reads the field under
    /// <c>_mutationLock</c>, where a stale zero would admit exactly the detach it exists to reject.
    /// </summary>
    internal int ReferenceCount => Volatile.Read(ref _referenceCount);

    private protected override void OnAddingFallbackContext(IInterceptorSubjectContext context)
    {
        // The predicate is "not the recorded attach context", not "no record": testing only for a
        // non-null record would accept AttachToContext(A) followed by AddFallbackContext(B) where B
        // carries a different lifecycle interceptor, and the subject would then resolve graph B's
        // interceptors while being absent from B's ledger and registry.
        if (ReferenceEquals(context, Volatile.Read(ref _attachContext)))
        {
            return;
        }

        if (!context.GetServices<ILifecycleInterceptor>().IsEmpty)
        {
            throw new InvalidOperationException(
                $"The context being added to subject '{_subject.GetType().FullName}' takes part in a lifecycle graph, " +
                "so adding it as a plain fallback context would leave the subject resolving that graph's interceptors " +
                $"while absent from its registry. Call {nameof(SubjectAttachmentExtensions.AttachToContext)} instead, " +
                "which publishes the edge and runs the attach callbacks together.");
        }
    }

    private protected override void OnRemovingFallbackContext(IInterceptorSubjectContext context)
    {
        if (ReferenceEquals(context, Volatile.Read(ref _attachContext)))
        {
            throw new InvalidOperationException(
                $"The context being removed from subject '{_subject.GetType().FullName}' is the context it was attached " +
                $"through, so removing it here would strand the subject in its lifecycle graph. Call " +
                $"{nameof(SubjectAttachmentExtensions.DetachFromContext)} instead, which runs the detach callbacks and " +
                "then removes the edge.");
        }
    }

    /// <summary>
    /// Records the attach before the edge is published, so the guard above sees a record naming this
    /// context by the time AttachToContext's own AddFallbackContext arrives. Returns false when the
    /// record already names this context, so a repeated attach is a no-op rather than a second pass.
    /// </summary>
    internal bool TryRecordAttachContext(IInterceptorSubjectContext context, ImmutableArray<ILifecycleInterceptor> interceptors)
    {
        lock (_mutationLock)
        {
            if (ReferenceEquals(_attachContext, context))
            {
                return false;
            }

            if (_attachContext is not null)
            {
                throw new InvalidOperationException(
                    $"Subject '{_subject.GetType().FullName}' is already attached through a different context. Detach it " +
                    $"with {nameof(SubjectAttachmentExtensions.DetachFromContext)}, passing the context that " +
                    $"{nameof(SubjectAttachmentExtensions.TryGetAttachContext)} returns, before attaching it elsewhere.");
            }

            // A check rather than a claim, so it races a concurrent claim. It is what makes the
            // deterministic misuse case publish nothing: without it, root-attaching a
            // property-owned subject into a second graph would set the record and the edge and only
            // then be rejected.
            if (_owner is not null && !interceptors.Contains(_owner))
            {
                throw new InvalidOperationException(
                    $"Subject '{_subject.GetType().FullName}' already belongs to another lifecycle graph. A subject belongs " +
                    "to at most one graph; remove it from its current graph before attaching it to this one.");
            }

            _attachContext = context;
            _attachInterceptors = interceptors;
            return true;
        }
    }

    /// <summary>
    /// Clears the record and returns the interceptor set the attach resolved, from inside the same
    /// critical section that picks the winner. Two concurrent detaches both take the lock; one finds
    /// the record and proceeds, the other finds null and returns having called nothing. Reading the
    /// set before this call would be check-then-act across a lock boundary and could enumerate a
    /// default ImmutableArray.
    ///
    /// The reference-count guard runs here for the same reason. IncrementReferenceCount takes this
    /// lock, so a caller reading the count first and clearing the record after would let a property
    /// attach land in between: the root detach would then clear the record, run the detach
    /// interceptors and remove the edge on a subject that has just become a child, which is exactly
    /// the state the guard rejects.
    /// </summary>
    internal bool TryClearAttachContext(IInterceptorSubjectContext context, out ImmutableArray<ILifecycleInterceptor> interceptors)
    {
        lock (_mutationLock)
        {
            // Before the record checks, so a still-referenced subject is rejected whether or not it
            // was ever root-attached, and nothing has been cleared when it is.
            if (_referenceCount != 0)
            {
                throw new InvalidOperationException(
                    $"Subject '{_subject.GetType().FullName}' is still referenced from {_referenceCount} parent " +
                    "property/properties, so it cannot be detached as a root. Remove those references first; the " +
                    "subject then leaves the graph on its own.");
            }

            if (_attachContext is null)
            {
                interceptors = ImmutableArray<ILifecycleInterceptor>.Empty;
                return false;
            }

            if (!ReferenceEquals(_attachContext, context))
            {
                throw new InvalidOperationException(
                    $"Subject '{_subject.GetType().FullName}' was not attached through the given context, so detaching it " +
                    "from that context would do nothing. Pass the context it was attached through, which " +
                    $"{nameof(SubjectAttachmentExtensions.TryGetAttachContext)} returns.");
            }

            interceptors = _attachInterceptors;
            _attachContext = null;
            _attachInterceptors = ImmutableArray<ILifecycleInterceptor>.Empty;
            return true;
        }
    }

    /// <summary>Rolls back a failed attach: clears the record and removes the edge it published.</summary>
    internal void ClearAttachContext(IInterceptorSubjectContext context)
    {
        lock (_mutationLock)
        {
            if (!ReferenceEquals(_attachContext, context))
            {
                return;
            }

            _attachContext = null;
            _attachInterceptors = ImmutableArray<ILifecycleInterceptor>.Empty;
        }

        RemoveAttachEdge(context);
    }

    /// <summary>
    /// Releases whatever attach edge the subject holds, silently. Called when the subject leaves the
    /// graph by the property route, where the descent has already detached it, so its own graph
    /// loses nothing. A second interceptor co-registered on the attach context does lose the
    /// notification master's executor override gave it.
    /// </summary>
    internal void ReleaseAttachEdge()
    {
        IInterceptorSubjectContext? context;

        lock (_mutationLock)
        {
            context = _attachContext;
            if (context is null)
            {
                return;
            }

            _attachContext = null;
            _attachInterceptors = ImmutableArray<ILifecycleInterceptor>.Empty;
        }

        RemoveAttachEdge(context);
    }

    /// <summary>
    /// The check and the claim in one critical section. Two graphs hold two different
    /// <c>_attachedSubjects</c> monitors but contend for this same lock, so no interleaving of their
    /// monitors can beat it.
    ///
    /// A graph is a set of co-resolved interceptors, not a single instance: aggregating two contexts
    /// that each register tracking resolves two <c>LifecycleInterceptor</c>s that both attach every
    /// subject, so the claim is rejected only when the standing owner does not resolve from the
    /// context the new claim comes through. That is the same predicate
    /// <see cref="TryRecordAttachContext"/> already applies to its own resolved set, and it is
    /// evaluated only when the two differ, so an ordinary claim stays a field read.
    ///
    /// Two consequences, both confined to aggregated configurations that already share an
    /// interceptor; the rejection of genuinely distinct graphs is unaffected. The predicate is
    /// asymmetric: a context that resolves the standing owner may claim, one that does not resolve
    /// it may not, and since the owner is whoever claims first among co-resolved
    /// interceptors the outcome depends on resolved interceptor order. And because
    /// <c>ThrowIfDetachIsUnwinding</c> is gated on ownership, only the owning interceptor enforces
    /// the re-attach-during-detach rejection, so in a two-interceptor aggregate a re-attach during
    /// the non-owner's unwind passes both that guard and this claim.
    /// </summary>
    internal void ClaimOwnership(ILifecycleInterceptor owner, IInterceptorSubjectContext context)
    {
        lock (_mutationLock)
        {
            if (_owner is null)
            {
                _owner = owner;
                return;
            }

            if (!ReferenceEquals(_owner, owner) && !context.GetServices<ILifecycleInterceptor>().Contains(_owner))
            {
                throw new InvalidOperationException(
                    $"Subject '{_subject.GetType().FullName}' already belongs to another lifecycle graph. A subject belongs " +
                    "to at most one graph; remove it from its current graph before referencing it from this one.");
            }
        }
    }

    /// <summary>
    /// Whether the given interceptor is the one holding the ownership claim. Lets a graph tell its
    /// own bookkeeping apart from a co-resolved interceptor's, which is what the re-attach guard in
    /// <c>LifecycleInterceptor</c> needs.
    /// </summary>
    internal bool IsOwnedBy(ILifecycleInterceptor owner)
    {
        return ReferenceEquals(Volatile.Read(ref _owner), owner);
    }

    /// <summary>
    /// Released on graph membership, mirroring <see cref="ClaimOwnership"/>: the caller may be the
    /// owner, or the context it detaches through may resolve the standing owner. Identity alone is
    /// not enough, because the claim is taken by the first co-resolved interceptor to attach while
    /// the release is driven by the last one to bring the reference count to zero, and in an
    /// aggregated configuration those are two different instances. Releasing on identity there is a
    /// permanent no-op, which leaves the subject owned with no references, reporting attached and
    /// unable to join any other graph.
    ///
    /// A disjoint graph still cannot resolve the owner, so a detach in one graph can never clear a
    /// claim another graph holds.
    /// </summary>
    internal void ReleaseOwnership(ILifecycleInterceptor owner, IInterceptorSubjectContext context)
    {
        lock (_mutationLock)
        {
            var currentOwner = _owner;
            if (currentOwner is null)
            {
                return;
            }

            if (ReferenceEquals(currentOwner, owner) || context.GetServices<ILifecycleInterceptor>().Contains(currentOwner))
            {
                _owner = null;
            }
        }
    }

    internal int IncrementReferenceCount()
    {
        lock (_mutationLock)
        {
            var count = _referenceCount + 1;
            Volatile.Write(ref _referenceCount, count);
            return count;
        }
    }

    internal int DecrementReferenceCount()
    {
        lock (_mutationLock)
        {
            var count = _referenceCount > 0 ? _referenceCount - 1 : 0;
            Volatile.Write(ref _referenceCount, count);
            return count;
        }
    }
}