using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Cache;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Ordering;

namespace Namotion.Interceptor;

public class InterceptorSubjectContext : IInterceptorSubjectContext
{
    // All topology (services, fallback contexts) and everything derived from it (delegation
    // target, caches, compiled interceptor chains) lives in one immutable snapshot per context,
    // published atomically. Queries take no context lock (R1): a query pins one snapshot with a
    // single volatile read and walks other contexts' snapshots the same way, so the downward
    // service walk and the upward invalidation walk cannot form a lock cycle, including in cyclic
    // fallback graphs. Filling a cache still takes the internal bucket lock of a
    // ConcurrentDictionary, which is a leaf and never spans two contexts.
    //
    // Lock order: _mutationLock -> a _usedByContexts set lock, never reverse. A set lock is a
    // leaf: its critical sections only touch that one set and never take another lock or call
    // into another context. That leaf property is what makes per-context set locks safe where a
    // single global one was: the wait graph has no edge out of a set lock, so two contexts
    // registering into each other concurrently cannot form a cycle.
    //
    // Two _mutationLock objects are never ordered against each other because no path in this class
    // acquires a second one: mutators only touch another context's using set (a leaf lock) and the
    // service walk is lock-free. The single way to nest them is a TryAddService factory or exists
    // predicate that mutates a different context, which the public contract forbids for that reason
    // (see IInterceptorSubjectContext.TryAddService).

    // Delegation hops walked without any cycle bookkeeping. Chains can be as long as the subject
    // graph is deep, so this is not a bound on anything: past it the walk keeps going under Floyd
    // detection, which costs a second pointer and no memory. The value only trades how quickly a
    // cyclic chain reaches detection against how many hops stay free of it.
    private const int UncheckedDelegationHops = 8;

    [ThreadStatic]
    private static HashSet<InterceptorSubjectContext>? _invalidationVisited;

    [ThreadStatic]
    private static List<InterceptorSubjectContext>? _invalidationPending;

    [ThreadStatic]
    private static HashSet<InterceptorSubjectContext>? _serviceQueryVisited;

    [ThreadStatic]
    private static HashSet<InterceptorSubjectContext>? _delegationCycleVisited;

    [ThreadStatic]
    private static List<InterceptorSubjectContext>? _delegationCyclePath;

    // Written via Interlocked.Exchange and Interlocked.CompareExchange instead of being declared
    // volatile, which would raise CS0420 when passed by ref to Interlocked under
    // warnings-as-errors. Every context constructs its own initial state because caches live on
    // the state: one shared empty instance would let unrelated contexts contaminate each other's
    // caches.
    private ContextState _state = new(ImmutableArray<object>.Empty, ImmutableArray<InterceptorSubjectContext>.Empty);

    // Serializes mutators; never held on a query path.
    private readonly object _mutationLock = new();

    // Contexts that resolve through this context, lazily allocated because most contexts are
    // never used as a fallback. The set instance is its own lock: it is created once via CAS and
    // never replaced, so every thread locks the same canonical object without a second allocation.
    private HashSet<InterceptorSubjectContext>? _usedByContexts;

    public static InterceptorSubjectContext Create()
    {
        return new InterceptorSubjectContext();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ImmutableArray<TInterface> GetServices<TInterface>()
    {
        var state = Volatile.Read(ref _state);
        var delegationTarget = state.DelegationTarget;
        var resolved = this;
        if (delegationTarget is not null)
        {
            resolved = ResolveDelegationTarget(delegationTarget, out state);
        }

        return resolved.GetServicesFromState<TInterface>(state);
    }

    public virtual bool AddFallbackContext(IInterceptorSubjectContext context)
    {
        var contextImpl = (InterceptorSubjectContext)context;

        lock (_mutationLock)
        {
            var state = Volatile.Read(ref _state);
            if (state.FallbackContexts.Contains(contextImpl))
            {
                return false;
            }

            // R4: register into the fallback BEFORE publishing so that its _usedByContexts is
            // always a superset of the true using set. An extra entry only costs a spurious
            // invalidation; a missing entry would leave a compiled chain above permanently stale.
            var usedByContexts = contextImpl.GetOrCreateUsedByContexts();
            lock (usedByContexts)
            {
                usedByContexts.Add(this);
            }

            PublishState(new ContextState(state.Services, state.FallbackContexts.Add(contextImpl)));
        }

        InvalidateUsingContexts();
        return true;
    }

    protected bool HasFallbackContext(IInterceptorSubjectContext context)
    {
        return context is InterceptorSubjectContext contextImpl &&
               Volatile.Read(ref _state).FallbackContexts.Contains(contextImpl);
    }

    public virtual bool RemoveFallbackContext(IInterceptorSubjectContext context)
    {
        var contextImpl = (InterceptorSubjectContext)context;

        lock (_mutationLock)
        {
            var state = Volatile.Read(ref _state);
            var index = state.FallbackContexts.IndexOf(contextImpl);
            if (index < 0)
            {
                return false;
            }

            PublishState(new ContextState(state.Services, state.FallbackContexts.RemoveAt(index)));

            // R4: unregister from the fallback only AFTER publishing so that its _usedByContexts
            // stays a superset of the true using set for the whole transition (see
            // AddFallbackContext).
            var usedByContexts = Volatile.Read(ref contextImpl._usedByContexts);
            if (usedByContexts is not null)
            {
                lock (usedByContexts)
                {
                    usedByContexts.Remove(this);
                }
            }
        }

        InvalidateUsingContexts();
        return true;
    }

    public bool TryAddService<TService>(Func<TService> factory, Func<TService, bool> exists)
    {
        lock (_mutationLock)
        {
            var state = Volatile.Read(ref _state);

            // The lock-free walk keeps the check atomic against concurrent mutators of this
            // context because they all serialize on _mutationLock.
            if (ComputeServices<TService>(state).Any(exists))
            {
                return false;
            }

            var service = factory();

            // The factory may reenter this context (Monitor is reentrant) and publish a mutation,
            // so re-read the state to not lose it. A factory registering the same service type
            // into the same context is its own responsibility; mutating a different context from
            // here is forbidden, see the lock order note at the top of the class.
            state = Volatile.Read(ref _state);
            PublishState(new ContextState(state.Services.Add(service!), state.FallbackContexts));
        }

        InvalidateUsingContexts();
        return true;
    }

    public void AddService<TService>(TService service)
    {
        lock (_mutationLock)
        {
            var state = Volatile.Read(ref _state);
            PublishState(new ContextState(state.Services.Add(service!), state.FallbackContexts));
        }

        InvalidateUsingContexts();
    }

    public TInterface? TryGetService<TInterface>()
    {
        var services = GetServices<TInterface>();
        return services.Length switch
        {
            1 => services[0],
            0 => default,
            _ => throw new InvalidOperationException($"There must be exactly one service of type {typeof(TInterface).FullName}.")
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal TProperty ExecuteInterceptedRead<TProperty>(ref PropertyReadContext<TProperty> context, Func<IInterceptorSubject, TProperty> readValue)
    {
        var state = Volatile.Read(ref _state);
        var delegationTarget = state.DelegationTarget;
        var resolved = this;
        if (delegationTarget is not null)
        {
            resolved = ResolveDelegationTarget(delegationTarget, out state);
        }

        var function = resolved.GetReadInterceptorFunction<TProperty>(state);
        return function(ref context, readValue);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ExecuteInterceptedWrite<TProperty>(ref PropertyWriteContext<TProperty> context, Action<IInterceptorSubject, TProperty> writeValue)
    {
        var state = Volatile.Read(ref _state);
        var delegationTarget = state.DelegationTarget;
        var resolved = this;
        if (delegationTarget is not null)
        {
            resolved = ResolveDelegationTarget(delegationTarget, out state);
        }

        var action = resolved.GetWriteInterceptorFunction<TProperty>(state);
        action(ref context, writeValue);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal object? ExecuteInterceptedInvoke(ref MethodInvocationContext context, Func<IInterceptorSubject, object?[], object?> invokeMethod)
    {
        var state = Volatile.Read(ref _state);
        var delegationTarget = state.DelegationTarget;
        var resolved = this;
        if (delegationTarget is not null)
        {
            resolved = ResolveDelegationTarget(delegationTarget, out state);
        }

        var function = resolved.GetMethodInvocationFunction(state);
        return function(ref context, invokeMethod);
    }

    /// <summary>
    /// Walks the delegation chain that starts at <paramref name="delegationTarget"/> down to the
    /// first context that does not delegate, and returns it together with the state it was pinned
    /// on. A context with no own service and exactly one fallback context resolves everything
    /// through that fallback, so the chain is as deep as the subject tree that produced it: every
    /// child attached to a parent inherits the parent context as its fallback, which makes a graph
    /// of depth N a chain of length N. That rules out both recursion, which would put an unbounded
    /// stack depth on the hottest path in the library, and any fixed hop limit, which would reject
    /// a legitimate deep graph.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static InterceptorSubjectContext ResolveDelegationTarget(InterceptorSubjectContext delegationTarget, out ContextState state)
    {
        // The single hop stays inline: one pinned state and one branch, and one call frame less
        // than the recursive call site it replaces, which the JIT could not inline. Everything
        // deeper is out of line.
        state = Volatile.Read(ref delegationTarget._state);
        var next = state.DelegationTarget;
        return next is null ? delegationTarget : FollowDelegationChain(next, out state);
    }

    /// <summary>
    /// Iterative continuation of <see cref="ResolveDelegationTarget"/> for chains of more than one
    /// hop, with Floyd cycle detection beyond <see cref="UncheckedDelegationHops"/>: the hare takes
    /// two hops per tortoise hop, so a chain that closes into a cycle makes them meet after O(cycle
    /// length) hops using no memory, while a chain that merely is long never triggers it. Counting
    /// hops instead would need a ceiling, and no ceiling is correct for a chain whose legitimate
    /// length is the depth of the subject graph.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static InterceptorSubjectContext FollowDelegationChain(InterceptorSubjectContext delegationTarget, out ContextState state)
    {
        var hare = delegationTarget;
        var hareState = Volatile.Read(ref hare._state);

        for (var hop = 0; hop < UncheckedDelegationHops; hop++)
        {
            var next = hareState.DelegationTarget;
            if (next is null)
            {
                state = hareState;
                return hare;
            }

            hare = next;
            hareState = Volatile.Read(ref hare._state);
        }

        // Both start where the plain prefix ended rather than at the head. Every context has
        // exactly one delegation edge, so the walk is deterministic: a cycle reachable from the
        // head is still reachable from here, and if the prefix already entered one then this node
        // is on it. Floyd only needs the two pointers to start together.
        var tortoise = hare;
        var tortoiseState = hareState;

        while (true)
        {
            for (var step = 0; step < 2; step++)
            {
                var next = hareState.DelegationTarget;
                if (next is null)
                {
                    state = hareState;
                    return hare;
                }

                hare = next;
                hareState = Volatile.Read(ref hare._state);
            }

            var tortoiseNext = tortoiseState.DelegationTarget;
            if (tortoiseNext is null)
            {
                // The hare already crossed this edge, so it can only have disappeared through a
                // concurrent mutation. Putting the tortoise back on the hare keeps it behind the
                // hare on a chain that currently exists, which is all Floyd needs.
                tortoise = hare;
                tortoiseState = hareState;
                continue;
            }

            tortoise = tortoiseNext;
            tortoiseState = Volatile.Read(ref tortoise._state);

            if (ReferenceEquals(hare, tortoise))
            {
                // A meeting is proof of a cycle only in a graph that does not change underneath the
                // walk. Concurrent mutation can move the tortoise onto the hare over an edge the
                // hare never took, so the suspicion is confirmed exactly before it is reported,
                // and the confirmation re-reads the loop it found before reporting anything, since
                // a walk that reads each edge at its own time follows a path through time and not
                // a topology at an instant.
                return ResolveDelegationChainExactly(delegationTarget, out state);
            }
        }
    }

    /// <summary>
    /// Re-walks the chain remembering every context, which either reaches a context that does not
    /// delegate or observes one twice. Observing one twice is not yet a cycle: the walk reads each
    /// edge at its own time, so it follows a path through time rather than a topology at an
    /// instant, and two ordered rewirings that are each acyclic can produce a repeat that never
    /// existed as a cycle. Cutting an edge upstream of a walker and then linking a node downstream
    /// of it back to that upstream node is enough. So the candidate loop is re-read afterwards and
    /// only reported once its edges are all still in place. Only reached from a Floyd meeting, so
    /// none of this touches a resolving query.
    /// </summary>
    private static InterceptorSubjectContext ResolveDelegationChainExactly(InterceptorSubjectContext delegationTarget, out ContextState state)
    {
        var visited = _delegationCycleVisited ??= [];
        var path = _delegationCyclePath ??= [];
        try
        {
            while (true)
            {
                visited.Clear();
                path.Clear();

                var current = delegationTarget;
                var currentState = Volatile.Read(ref current._state);

                while (visited.Add(current))
                {
                    path.Add(current);

                    var next = currentState.DelegationTarget;
                    if (next is null)
                    {
                        state = currentState;
                        return current;
                    }

                    current = next;
                    currentState = Volatile.Read(ref current._state);
                }

                if (DelegationLoopStillClosed(path, current))
                {
                    throw new InvalidOperationException(
                        "The fallback contexts form a delegation cycle, so no service can be resolved. A context " +
                        "without own services and with exactly one fallback context resolves everything through " +
                        "that fallback context, and following those references leads back to a context already " +
                        "visited. Break the cycle by removing one of the fallback context registrations or by " +
                        "registering a service on one of the contexts on it.");
                }

                // The loop came apart under the walk, so it was a rewiring and not a cycle. A real
                // cycle has no edge to lose and confirms on the next pass.
            }
        }
        finally
        {
            visited.Clear();
            path.Clear();
        }
    }

    /// <summary>
    /// Re-reads every edge of the candidate loop that the walk recorded, after that walk finished.
    /// Because this pass starts once the whole loop has been observed, edges that all still point
    /// where they did held together across the two passes, which no single rewiring survives and
    /// which a genuine cycle always satisfies. It compares delegation targets rather than state
    /// objects, since invalidating a cache publishes a new state that leaves the topology alone.
    /// </summary>
    private static bool DelegationLoopStillClosed(List<InterceptorSubjectContext> path, InterceptorSubjectContext repeated)
    {
        for (var index = path.IndexOf(repeated); index < path.Count; index++)
        {
            var node = path[index];
            var expectedNext = index + 1 < path.Count ? path[index + 1] : repeated;
            if (!ReferenceEquals(Volatile.Read(ref node._state).DelegationTarget, expectedNext))
            {
                return false;
            }
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadFunc<TProperty> GetReadInterceptorFunction<TProperty>(ContextState state)
    {
        if (state.GetReadFunctions().TryGetValue(typeof(TProperty), out var cached))
        {
            return (ReadFunc<TProperty>)cached;
        }

        return CreateReadInterceptorFunction<TProperty>(state);
    }

    private ReadFunc<TProperty> CreateReadInterceptorFunction<TProperty>(ContextState state)
    {
        // Services are resolved from the same snapshot that caches the compiled chain, so a
        // topology change (which publishes a new state with fresh caches) can never keep a chain
        // that misses an interceptor.
        var readInterceptors = GetServicesFromState<IReadInterceptor>(state);
        var function = ReadInterceptorFactory<TProperty>.Create(readInterceptors);
        state.GetReadFunctions().TryAdd(typeof(TProperty), function);
        return function;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private WriteAction<TProperty> GetWriteInterceptorFunction<TProperty>(ContextState state)
    {
        if (state.GetWriteFunctions().TryGetValue(typeof(TProperty), out var cached))
        {
            return (WriteAction<TProperty>)cached;
        }

        return CreateWriteInterceptorFunction<TProperty>(state);
    }

    private WriteAction<TProperty> CreateWriteInterceptorFunction<TProperty>(ContextState state)
    {
        var writeInterceptors = GetServicesFromState<IWriteInterceptor>(state);
        var action = WriteInterceptorFactory<TProperty>.Create(writeInterceptors);
        state.GetWriteFunctions().TryAdd(typeof(TProperty), action);
        return action;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private InvokeFunc GetMethodInvocationFunction(ContextState state)
    {
        var cached = state.MethodInvocationFunction;
        if (cached is not null)
        {
            return (InvokeFunc)cached;
        }

        return CreateMethodInvocationFunction(state);
    }

    private InvokeFunc CreateMethodInvocationFunction(ContextState state)
    {
        var methodInterceptors = GetServicesFromState<IMethodInterceptor>(state);
        var function = MethodInvocationFactory.Create(methodInterceptors);

        // The CAS winner is returned so that every caller invokes the same canonical chain.
        return (InvokeFunc)state.SetMethodInvocationFunction(function);
    }

    /// <summary>
    /// Resolves services from the given pinned snapshot, whose delegation target the caller has
    /// already ruled out. The cache entry is computed from the same snapshot that owns the cache,
    /// so a topology change (which publishes a new state) can never receive a stale entry.
    /// </summary>
    private ImmutableArray<TInterface> GetServicesFromState<TInterface>(ContextState state)
    {
        // Empty contexts skip cache creation entirely so that fresh contexts stay allocation-free.
        if (state.IsEmpty)
        {
            return ImmutableArray<TInterface>.Empty;
        }

        var serviceCache = state.GetServiceCache();
        if (serviceCache.TryGetValue(typeof(TInterface), out var cachedServices))
        {
            return (ImmutableArray<TInterface>)cachedServices;
        }

        var services = ComputeServices<TInterface>(state);

        // The two-argument GetOrAdd canonicalizes racing computations without a closure allocation.
        return (ImmutableArray<TInterface>)serviceCache.GetOrAdd(typeof(TInterface), services);
    }

    private ImmutableArray<TInterface> ComputeServices<TInterface>(ContextState state)
    {
        var visited = _serviceQueryVisited ??= [];
        try
        {
            return CollectServices(typeof(TInterface), state, visited)
                .OfType<TInterface>()
                .ToImmutableArray();
        }
        finally
        {
            visited.Clear();
        }
    }

    private IEnumerable<object> CollectServices(Type type, ContextState state, HashSet<InterceptorSubjectContext> visited)
    {
        if (!visited.Add(this))
        {
            return [];
        }

        var delegationTarget = state.DelegationTarget;
        if (delegationTarget is not null)
        {
            return delegationTarget.CollectServices(type, Volatile.Read(ref delegationTarget._state), visited);
        }

        var services = state.Services
            .Where(type.IsInstanceOfType)
            .Concat(state.FallbackContexts.SelectMany(
                fallbackContext => fallbackContext.CollectServices(type, Volatile.Read(ref fallbackContext._state), visited)))
            .Distinct()
            .ToArray();

        return ServiceOrderResolver.OrderByDependencies(services);
    }

    /// <summary>
    /// R2: mutators publish under <see cref="_mutationLock"/>, no CAS loop. Mutators serialize on
    /// the lock, so none can lose another mutator's topology. The only lock-free writer is the
    /// invalidation CAS, which never changes topology; the state published here carries fresh
    /// caches, so overwriting a concurrent invalidation preserves its intent.
    ///
    /// The publish is an interlocked exchange rather than a volatile write because the publisher
    /// then reads the using sets and other contexts' states to drive the invalidation walk, and
    /// those reads must not be reordered before it. A release store plus Monitor.Exit does not
    /// order a later load: the memory model defines both as release-only, and CoreCLR implements
    /// Monitor.Exit release-only on Windows ARM64 before .NET 10, where an acquire load may then
    /// be satisfied from before the store. The consequence would be an invalidation skipped
    /// against a stale using set while the current state keeps accumulating cache fills computed
    /// from pre-mutation topology, leaving a compiled chain permanently missing an interceptor.
    /// The full fence closes that without touching the query path.
    /// </summary>
    private void PublishState(ContextState state)
    {
        Interlocked.Exchange(ref _state, state);
    }

    /// <summary>
    /// R3: one unconditional CAS attempt. No early-out when caches look absent, because a reader
    /// may be lazily creating a cache concurrently and skipping would let its insert survive the
    /// change. No retry on failure, because every competing write also publishes cache-free
    /// state, so the intent is satisfied either way.
    /// </summary>
    private void InvalidateState()
    {
        var current = Volatile.Read(ref _state);
        Interlocked.CompareExchange(ref _state, current.WithoutCaches(), current);
    }

    /// <summary>
    /// Returns the canonical using set of this context, creating it on first use. The CAS keeps
    /// one instance when two contexts register concurrently, which is what lets callers use the
    /// set itself as the lock.
    /// </summary>
    private HashSet<InterceptorSubjectContext> GetOrCreateUsedByContexts()
    {
        var usedByContexts = Volatile.Read(ref _usedByContexts);
        if (usedByContexts is not null)
        {
            return usedByContexts;
        }

        var created = new HashSet<InterceptorSubjectContext>();
        return Interlocked.CompareExchange(ref _usedByContexts, created, null) ?? created;
    }

    /// <summary>
    /// Invalidates every context that resolves through this one, walked with an explicit worklist
    /// rather than by recursion. The using graph is the fallback graph reversed, so it is as deep as
    /// the subject graph: every attached child inherits the context of its parent as a fallback
    /// context, which makes a graph of depth N a using chain of length N. Recursing over it died on
    /// an uncatchable <see cref="StackOverflowException"/> that no mutator could survive, while the
    /// worklist grows on the heap alongside the graph it walks.
    ///
    /// It terminates because a context is queued only when <c>visited</c> did not already hold it,
    /// so every context enters the worklist at most once and the loop removes one per iteration.
    /// That bound is what also makes the walk safe on a cyclic graph.
    /// </summary>
    private void InvalidateUsingContexts()
    {
        var visited = _invalidationVisited ??= [];
        var pending = _invalidationPending ??= [];
        try
        {
            // Self needs no invalidation here: the publish preceding this call already swapped
            // in a cache-free state.
            visited.Add(this);
            QueueUsingContexts(this, visited, pending);

            while (pending.Count != 0)
            {
                // Removing from the end keeps the worklist a stack, which costs no shifting. The
                // order between two contexts is not observable: invalidating one is an independent
                // CAS on its own state and never reads another context.
                var lastIndex = pending.Count - 1;
                var usingContext = pending[lastIndex];
                pending.RemoveAt(lastIndex);

                usingContext.InvalidateState();
                QueueUsingContexts(usingContext, visited, pending);
            }
        }
        finally
        {
            visited.Clear();
            pending.Clear();
        }
    }

    /// <summary>
    /// Queues the contexts that resolve through <paramref name="context"/> and have not been
    /// invalidated yet.
    /// </summary>
    private static void QueueUsingContexts(
        InterceptorSubjectContext context,
        HashSet<InterceptorSubjectContext> visited,
        List<InterceptorSubjectContext> pending)
    {
        // Contexts that are never used as a fallback take no lock at all. The field is written
        // once by a CAS that is ordered before the registrant's own publish, so a registration
        // racing this read either is visible here or belongs to a context that has not published
        // yet, and that context's own walk then covers everything above it. This depends on the
        // publish being a full fence, see PublishState: with a release-only store the read could
        // be satisfied from before it and the registration would be missed in both directions.
        //
        // The emptiness of the set is deliberately NOT checked here. HashSet.Count is a composite
        // of two independently mutated fields, so an unlocked read can compute a count that was
        // never true, and a using context that never left the set could be skipped for good. The
        // locked block below handles an empty set without allocating anyway.
        var usedByContexts = Volatile.Read(ref context._usedByContexts);
        if (usedByContexts is null)
        {
            return;
        }

        // Snapshot under the set lock, queue after release: calling into another context while
        // holding a set lock is forbidden by the lock order. The 0/1/many shapes avoid an array
        // allocation in the common cases.
        InterceptorSubjectContext? singleUsingContext = null;
        InterceptorSubjectContext[]? usingContexts = null;

        lock (usedByContexts)
        {
            if (usedByContexts.Count == 1)
            {
                // foreach binds the HashSet struct enumerator, First() would box it.
                foreach (var usingContext in usedByContexts)
                {
                    singleUsingContext = usingContext;
                    break;
                }
            }
            else if (usedByContexts.Count != 0)
            {
                usingContexts = [.. usedByContexts];
            }
        }

        if (singleUsingContext is not null)
        {
            if (visited.Add(singleUsingContext))
            {
                pending.Add(singleUsingContext);
            }
        }
        else if (usingContexts is not null)
        {
            foreach (var usingContext in usingContexts)
            {
                if (visited.Add(usingContext))
                {
                    pending.Add(usingContext);
                }
            }
        }
    }

    private sealed class ContextState
    {
        // Insertion order is kept and duplicate references are tolerated here; the Distinct()
        // in the service walk preserves the previous HashSet storage semantics.
        internal readonly ImmutableArray<object> Services;
        internal readonly ImmutableArray<InterceptorSubjectContext> FallbackContexts;

        // Derived in the constructor from the two fields above, so no reader can ever observe
        // it disagreeing with them.
        internal readonly InterceptorSubjectContext? DelegationTarget;

        // Caches belong to the state that produced them and are created lazily via CAS on first
        // use. A topology change publishes a new state, so a late insert from a concurrent
        // computation lands in the abandoned state and is never read again.
        private ConcurrentDictionary<Type, object>? _serviceCache; // stores ImmutableArray<T> boxed
        private ConcurrentDictionary<Type, Delegate>? _readFunctions;
        private ConcurrentDictionary<Type, Delegate>? _writeFunctions;
        private Delegate? _methodInvocationFunction;

        internal ContextState(ImmutableArray<object> services, ImmutableArray<InterceptorSubjectContext> fallbackContexts)
        {
            Services = services;
            FallbackContexts = fallbackContexts;
            DelegationTarget = services.IsEmpty && fallbackContexts.Length == 1 ? fallbackContexts[0] : null;
        }

        internal bool IsEmpty => Services.IsEmpty && FallbackContexts.IsEmpty;

        internal Delegate? MethodInvocationFunction
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Volatile.Read(ref _methodInvocationFunction);
        }

        internal ContextState WithoutCaches()
        {
            return new ContextState(Services, FallbackContexts);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ConcurrentDictionary<Type, object> GetServiceCache()
        {
            return Volatile.Read(ref _serviceCache) ?? InitializeServiceCache();
        }

        private ConcurrentDictionary<Type, object> InitializeServiceCache()
        {
            // The CAS keeps one canonical dictionary when two readers race the first use.
            var created = new ConcurrentDictionary<Type, object>();
            return Interlocked.CompareExchange(ref _serviceCache, created, null) ?? created;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ConcurrentDictionary<Type, Delegate> GetReadFunctions()
        {
            return Volatile.Read(ref _readFunctions) ?? InitializeReadFunctions();
        }

        private ConcurrentDictionary<Type, Delegate> InitializeReadFunctions()
        {
            var created = new ConcurrentDictionary<Type, Delegate>();
            return Interlocked.CompareExchange(ref _readFunctions, created, null) ?? created;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ConcurrentDictionary<Type, Delegate> GetWriteFunctions()
        {
            return Volatile.Read(ref _writeFunctions) ?? InitializeWriteFunctions();
        }

        private ConcurrentDictionary<Type, Delegate> InitializeWriteFunctions()
        {
            var created = new ConcurrentDictionary<Type, Delegate>();
            return Interlocked.CompareExchange(ref _writeFunctions, created, null) ?? created;
        }

        internal Delegate SetMethodInvocationFunction(Delegate function)
        {
            return Interlocked.CompareExchange(ref _methodInvocationFunction, function, null) ?? function;
        }
    }
}
