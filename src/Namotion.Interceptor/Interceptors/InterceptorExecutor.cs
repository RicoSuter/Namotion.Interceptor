using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Interceptors;

public sealed class InterceptorExecutor : InterceptorSubjectContext, IInterceptorExecutor
{
    private readonly IInterceptorSubject _subject;

    /// <summary>
    /// The subject this executor was constructed for. Exposed so the terminal write can assert that the
    /// context's executor and the locked subject are the same pairing its plain increment relies on.
    /// </summary>
    internal IInterceptorSubject Subject => _subject;

    /// <summary>
    /// Monotonic per-subject commit counter. Incremented by the terminal write while the subject's
    /// SyncRoot is held, so a plain increment is exclusive: no Interlocked needed. Dense over
    /// committed writes and never reset, so it stays comparable across detach and reattach.
    ///
    /// It records commit order, it does not establish it: the lock is what serializes the commits, and
    /// this counter labels them afterwards. Consumers do order changes by comparing it (see the flush
    /// merging in docs/tracking.md), but nothing in the write path depends on its value.
    ///
    /// Consumes a revision exactly when the terminal write runs. A vetoed write and a write stopped
    /// by the equality check never reach the terminal and consume nothing, but a derived property's
    /// recalculation does reach it (with a no-op write delegate) and takes a revision of its own.
    /// </summary>
    internal long Revision;

    public InterceptorExecutor(IInterceptorSubject subject)
    {
        _subject = subject;
    }

    /// <summary>
    /// Returns the subject's executor, publishing one on first access. Call it from the subject's
    /// <see cref="IInterceptorSubject.Context"/> accessor, passing that subject's own backing field.
    /// Public because the source generator emits the call into the consumer's assembly.
    /// </summary>
    /// <remarks>
    /// Compare-and-swap rather than <c>??=</c>: a lazy assignment lets two threads racing the first
    /// access each publish an executor and discard one, along with everything that had been put on it,
    /// including the per-subject commit revision counter. It is also the store that safely publishes
    /// the new instance, which a plain assignment is not.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IInterceptorExecutor GetOrCreate(ref IInterceptorExecutor? context, IInterceptorSubject subject)
    {
        // The allocation sits in a separate non-inlined method so the accessor stays a load and a
        // branch, small enough to inline into its own callers.
        return context ?? CreateAndPublish(ref context, subject);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IInterceptorExecutor CreateAndPublish(ref IInterceptorExecutor? context, IInterceptorSubject subject)
    {
        var created = new InterceptorExecutor(subject);
        return Interlocked.CompareExchange(ref context, created, null) ?? created;
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
            this,
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
    /// The new value is already the stabilized getter output on this path, so publishing reuses it
    /// instead of invoking the getter again (see
    /// <see cref="PropertyWriteContext{TProperty}.FinalValueIsNewValue"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool SetPropertyValue<TProperty>(string propertyName, TProperty newValue, TProperty currentValue, Action<IInterceptorSubject, TProperty> writeValue, long rawTimestamp)
    {
        var context = new PropertyWriteContext<TProperty>(
            this,
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

    // Interceptors that claimed this subject while a different one already held the claim, so the
    // release can answer from a field instead of resolving. Null while nobody but the standing
    // owner has claimed, which is every single-interceptor configuration, so the common path
    // allocates nothing. A list rather than a set because it holds one entry per co-resolved
    // interceptor beyond the first, and because a manual scan gives reference equality without a
    // comparer instance.
    private List<ILifecycleInterceptor>? _coClaimants;
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
            // A repeated attach naming the context already recorded stays the documented no-op, so
            // that check comes first. It writes no record, which is what the count guard below
            // exists to prevent, so nothing is lost by letting it through: only a record
            // transition can reopen the race.
            if (ReferenceEquals(_attachContext, context))
            {
                return false;
            }

            // The mirror of the guard in TryClearAttachContext: a still-referenced subject is
            // rejected before any record is written. Two things make it worth having.
            //
            // It rejects an operation whose success is worse than its failure. Root-attaching an
            // already-referenced subject runs AttachSubjectToContext, which re-runs
            // FindSubjectsInProperties in Seed mode over a subtree that is already attached and
            // overwrites its reconciliation baseline from the backing store. Sequentially that is
            // invisible, because the child attaches no-op. Against a property write that next() has
            // already committed it is not: the re-seed makes the writer's reconciliation
            // early-return, and the old child is then never detached.
            //
            // And it closes the last sliver of the ReleaseAttachEdge race. That call compares
            // against the record captured when the count reached zero, so a record written after
            // the decrement survives; a record written before it still does not. This guard makes
            // that unreachable, because a subject whose count is about to be decremented to zero
            // still has a non-zero count when the racing attach tries to record. Reading the count
            // under this same lock is what makes it airtight: IncrementReferenceCount takes it too.
            if (_referenceCount != 0)
            {
                throw new InvalidOperationException(
                    $"Subject '{_subject.GetType().FullName}' is already referenced from {_referenceCount} parent " +
                    "property/properties, so it cannot be attached as a root. Attach it before referencing it from a " +
                    "parent property, or let it inherit the graph through its parent instead of root-attaching it.");
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
                    "to at most one graph; remove it from its current graph before attaching it to this one, by clearing " +
                    "the parent properties that reference it there or, if it is a root of that graph, by calling " +
                    $"{nameof(SubjectAttachmentExtensions.DetachFromContext)} with the context " +
                    $"{nameof(SubjectAttachmentExtensions.TryGetAttachContext)} returns.");
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
    /// Releases the attach edge the subject held when its reference count was decided, silently.
    /// Called when the subject leaves the graph by the property route, where the descent has already
    /// detached it, so its own graph loses nothing. A second interceptor co-registered on the attach
    /// context does lose the notification master's executor override gave it.
    /// </summary>
    /// <param name="expectedContext">
    /// The record <see cref="DecrementReferenceCount"/> observed while it held the lock, or null when
    /// there was none, in which case there is nothing to release.
    /// </param>
    internal void ReleaseAttachEdge(IInterceptorSubjectContext? expectedContext)
    {
        if (expectedContext is null)
        {
            return;
        }

        lock (_mutationLock)
        {
            // Compared against the captured record rather than releasing whatever is live: the
            // decrement and this call are separated by the detach handlers, and a concurrent
            // AttachToContext can record and publish its own edge in between. Releasing that one
            // would leave the subject owned and in the ledger while resolving nothing, with no
            // record left for DetachFromContext to act on.
            if (!ReferenceEquals(_attachContext, expectedContext))
            {
                return;
            }

            _attachContext = null;
            _attachInterceptors = ImmutableArray<ILifecycleInterceptor>.Empty;
        }

        RemoveAttachEdge(expectedContext);
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
    /// interceptors the outcome depends on resolved interceptor order, and the root route and the
    /// property route claim in opposite directions, so the same context pair accepts one and rejects
    /// the other. Both are pinned by AggregatedContextLifecycleTests.
    ///
    /// <c>ThrowIfDetachIsUnwinding</c> is gated on ownership, which reads as though only the owning
    /// interceptor enforces the re-attach rejection. Measured, it is not a hole: co-resolved
    /// interceptors unwind inside the same write, so the owner's own guard fires whichever unwind
    /// the handler re-attaches from.
    ///
    /// An accepted claim from an interceptor that is not the standing owner is recorded, which is
    /// what lets <see cref="ReleaseOwnership"/> answer the same question later without resolving
    /// anything. The resolve happens here, on a path that may throw and whose caller is prepared
    /// for it, and never again.
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

            if (ReferenceEquals(_owner, owner))
            {
                return;
            }

            if (!context.GetServices<ILifecycleInterceptor>().Contains(_owner))
            {
                throw new InvalidOperationException(
                    $"Subject '{_subject.GetType().FullName}' already belongs to another lifecycle graph. A subject belongs " +
                    "to at most one graph; remove it from its current graph before referencing it from this one, by clearing " +
                    "the parent properties that reference it there or, if it is a root of that graph, by calling " +
                    $"{nameof(SubjectAttachmentExtensions.DetachFromContext)} with the context " +
                    $"{nameof(SubjectAttachmentExtensions.TryGetAttachContext)} returns.");
            }

            var coClaimants = _coClaimants ??= new List<ILifecycleInterceptor>(1);
            for (var index = 0; index < coClaimants.Count; index++)
            {
                if (ReferenceEquals(coClaimants[index], owner))
                {
                    return;
                }
            }

            coClaimants.Add(owner);
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
    /// Released by the standing owner or by an interceptor <see cref="ClaimOwnership"/> recorded
    /// alongside it. Identity alone is not enough, because the claim is taken by the first
    /// co-resolved interceptor to attach while the release is driven by the last one to bring the
    /// reference count to zero, and in an aggregated configuration those are two different
    /// instances. Releasing on identity there is a permanent no-op, which leaves the subject owned
    /// with no references, reporting attached and unable to join any other graph.
    ///
    /// It answers from the recorded set rather than resolving the detaching context, and that is
    /// the whole point: both call sites are <c>finally</c> blocks, and resolving a chain that has
    /// since been rewired into a pure-delegation loop throws from there, masking the exception
    /// already in flight and leaving the claim standing with no way to clear it. Field and list
    /// operations under <c>_mutationLock</c> cannot throw, so the release always completes.
    ///
    /// The recorded set is a stricter predicate than a resolve: it answers "this interceptor
    /// claimed" where a resolve answers "some context can still reach the owner". Every caller has
    /// necessarily claimed, because both call sites run only after the caller removed its own
    /// ledger entry, and only a successful claim can have created one. A disjoint graph therefore
    /// still cannot clear a claim another graph holds.
    ///
    /// The set is dropped with the claim, so the two can never disagree: while <c>_owner</c> is
    /// null the set is null too, and a release by an interceptor that claimed under a since-cleared
    /// owner finds no owner and returns.
    /// </summary>
    internal void ReleaseOwnership(ILifecycleInterceptor owner)
    {
        lock (_mutationLock)
        {
            var currentOwner = _owner;
            if (currentOwner is null)
            {
                return;
            }

            if (!ReferenceEquals(currentOwner, owner))
            {
                var coClaimants = _coClaimants;
                if (coClaimants is null)
                {
                    return;
                }

                var isCoClaimant = false;
                for (var index = 0; index < coClaimants.Count; index++)
                {
                    if (ReferenceEquals(coClaimants[index], owner))
                    {
                        isCoClaimant = true;
                        break;
                    }
                }

                if (!isCoClaimant)
                {
                    return;
                }
            }

            _owner = null;
            _coClaimants = null;
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

    /// <summary>
    /// Reports the attach record alongside the new count, from inside the critical section that
    /// decides the count, so the caller that observes zero can release exactly the record that was
    /// present at that instant rather than whatever a concurrent attach has written since.
    /// </summary>
    internal int DecrementReferenceCount(out IInterceptorSubjectContext? attachContext)
    {
        lock (_mutationLock)
        {
            var count = _referenceCount > 0 ? _referenceCount - 1 : 0;
            Volatile.Write(ref _referenceCount, count);
            attachContext = _attachContext;
            return count;
        }
    }
}