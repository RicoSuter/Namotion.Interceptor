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

    private const int MaximumRetainedTraversalSize = 1024;

    // Declared before the marker below, which constructs a context and must not read it at zero.
    private static int _lastPropertyTypeIndex = -1;

    // Recorded on a state whose chain was proven cyclic, so the verdict costs one walk per state
    // rather than one per query. A context rather than a marker object so that the slot can be
    // typed: this class is not sealed, so a type test on an object slot compiles to a runtime
    // helper call on every intercepted access.
    private static readonly InterceptorSubjectContext CyclicDelegationMarker = CreateCyclicDelegationMarker();

    /// <summary>
    /// A dense index per intercepted property type, so that a compiled chain is found by indexing
    /// an array instead of hashing a <see cref="Type"/>. Being a static of a generic type, it is
    /// computed once per property type in the process rather than once per access. Handing the
    /// indices out process wide is what keeps the lookup a plain array read; the cost is that an
    /// array is as long as the largest index its context has seen rather than as long as the number
    /// of types that context uses.
    /// </summary>
    private static class PropertyTypeIndex<TProperty>
    {
        internal static readonly int Value = Interlocked.Increment(ref _lastPropertyTypeIndex);
    }

    [ThreadStatic]
    private static HashSet<InterceptorSubjectContext>? _invalidationVisited;

    [ThreadStatic]
    private static List<InterceptorSubjectContext>? _invalidationPending;

    [ThreadStatic]
    private static HashSet<InterceptorSubjectContext>? _serviceQueryVisited;

    [ThreadStatic]
    private static HashSet<InterceptorSubjectContext>? _delegationCycleVisited;

    [ThreadStatic]
    private static List<DelegationHop>? _delegationCyclePath;

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

    /// <summary>
    /// Restricts context inheritance to this assembly because topology tracking requires context
    /// reference identity.
    /// </summary>
    private protected InterceptorSubjectContext()
    {
    }

    /// <summary>
    /// Creates a new interceptor subject context.
    /// </summary>
    /// <returns>The newly created context.</returns>
    public static InterceptorSubjectContext Create()
    {
        return new InterceptorSubjectContext();
    }

    private static InterceptorSubjectContext CreateCyclicDelegationMarker()
    {
        var marker = new InterceptorSubjectContext();
        marker.AddFallbackContext(marker);
        return marker;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ImmutableArray<TInterface> GetServices<TInterface>()
    {
        var state = Volatile.Read(ref _state);
        var resolved = this;
        if (state.DelegationTarget is not null)
        {
            resolved = ResolveDelegationTarget(ref state);
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
            // always a superset of the true using set. A missing entry would leave a compiled chain
            // above permanently stale. An extra entry costs a spurious invalidation, and lets the
            // invalidation walk reach a context out of chain order, which is why no walk may trust
            // what a context further down recorded about its chain (see ResolveDelegationChain).
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
        var resolved = this;
        if (state.DelegationTarget is not null)
        {
            resolved = ResolveDelegationTarget(ref state);
        }

        var function = resolved.GetReadInterceptorFunction<TProperty>(state);
        return function(ref context, readValue);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ExecuteInterceptedWrite<TProperty>(ref PropertyWriteContext<TProperty> context, Action<IInterceptorSubject, TProperty> writeValue)
    {
        var state = Volatile.Read(ref _state);
        var resolved = this;
        if (state.DelegationTarget is not null)
        {
            resolved = ResolveDelegationTarget(ref state);
        }

        var action = resolved.GetWriteInterceptorFunction<TProperty>(state);
        action(ref context, writeValue);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal object? ExecuteInterceptedInvoke(ref MethodInvocationContext context, Func<IInterceptorSubject, object?[], object?> invokeMethod)
    {
        var state = Volatile.Read(ref _state);
        var resolved = this;
        if (state.DelegationTarget is not null)
        {
            resolved = ResolveDelegationTarget(ref state);
        }

        var function = resolved.GetMethodInvocationFunction(state);
        return function(ref context, invokeMethod);
    }

    /// <summary>
    /// Resolves the context that answers for this one and replaces <paramref name="state"/> with
    /// the state that context was pinned on. A context with no own
    /// service and exactly one fallback context resolves everything through it, so the chain is as
    /// deep as the subject graph: every attached child inherits the context of its parent.
    ///
    /// The chain is walked once per state rather than once per query, which is what makes depth
    /// free here. The record holds the terminal CONTEXT and never its state: a context's state is
    /// replaced whenever anything below it changes, so re-reading it on every query is what keeps
    /// the answer fresh, and that read cannot be removed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private InterceptorSubjectContext ResolveDelegationTarget(ref ContextState state)
    {
        var terminal = state.ResolvedTerminal;
        if (terminal is not null && !ReferenceEquals(terminal, CyclicDelegationMarker))
        {
            var terminalState = Volatile.Read(ref terminal._state);

            // Fails for a context that started delegating since it was recorded, which the walk
            // below then corrects.
            if (terminalState.DelegationTarget is null)
            {
                state = terminalState;
                return terminal;
            }
        }

        return ResolveDelegationChain(ref state);
    }

    /// <summary>
    /// Walks the chain and records where it ends on every state it passed, which is what makes
    /// building a graph of depth N cost one walk instead of one per level. Iterative because the
    /// chain is as deep as the subject graph, so recursion overflows the stack and no fixed hop
    /// limit is correct.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private InterceptorSubjectContext ResolveDelegationChain(ref ContextState state)
    {
        var visited = _delegationCycleVisited ??= [];
        var path = _delegationCyclePath ??= [];
        try
        {
            while (true)
            {
                visited.Clear();
                path.Clear();

                // Re-pinned every pass, never reused from the caller: a stale first hop would make
                // every pass reach the same repeat and fail the same confirmation, spinning here
                // for good after one mutation.
                var current = this;
                var currentState = Volatile.Read(ref current._state);

                // Only the entry's own record may be trusted, and only for the chain that entry
                // still has. Everything below is read for topology alone, which is the one thing an
                // installed state always describes correctly. A record is a claim about contexts
                // beyond the one carrying it, and an invalidation walk can reach a context out of
                // chain order, because R4 keeps a using set a superset: RemoveFallbackContext
                // publishes before it unregisters, so a context that now delegates can still sit in
                // the using set it is leaving. Trusting a record from further down would then cache
                // a resolution of a context the graph no longer reaches, on a state that nothing
                // invalidates again.
                if (ReferenceEquals(currentState.ResolvedTerminal, CyclicDelegationMarker))
                {
                    throw CreateDelegationCycleException();
                }

                while (true)
                {
                    var next = currentState.DelegationTarget;
                    if (next is null)
                    {
                        CacheResolvedTerminal(path, current, 0);
                        state = currentState;
                        return current;
                    }

                    if (!visited.Add(current))
                    {
                        break;
                    }

                    path.Add(new DelegationHop(current, currentState));
                    current = next;
                    currentState = Volatile.Read(ref current._state);
                }

                if (DelegationLoopStillClosed(path, current, out var loopStart))
                {
                    // Only from the loop, never from the acyclic run that led into it: the
                    // confirmation re-reads the states of the loop and of nothing else, so a
                    // context ahead of it reaches the loop only according to an edge read earlier
                    // and possibly rewired since. Marking one of those would make every query on it
                    // raise until a pending invalidation replaces its state, and a caller cannot
                    // tell that from a cycle it really is on.
                    CacheResolvedTerminal(path, CyclicDelegationMarker, loopStart);
                    throw CreateDelegationCycleException();
                }

                // The loop came apart under the walk, so it was a rewiring and not a cycle. A real
                // cycle has no state to lose and confirms on the next pass.
            }
        }
        finally
        {
            // Dropped rather than cleared past the threshold: Clear() keeps the capacity, so one
            // walk down a deep chain would hold an entry per level on this thread for the rest of
            // the process, on every thread that ever touches such a graph.
            if (path.Capacity > MaximumRetainedTraversalSize)
            {
                _delegationCycleVisited = null;
                _delegationCyclePath = null;
            }
            else
            {
                visited.Clear();
                path.Clear();
            }
        }
    }

    /// <summary>
    /// Records where the chain ends from <paramref name="startIndex"/> onwards, which turns the
    /// next query on any of those contexts into a single hop. Written only to the objects the walk
    /// pinned and never to a re-read of a context's current state.
    ///
    /// What this guarantees is quiescent, not instantaneous. The walk reads each edge at its own
    /// time, so the chain it records may never have existed all at once, and a query overlapping
    /// two mutations can be answered from it. It converges because any change below a context
    /// replaces that context's state, so a record that disagrees with the topology sits on a state
    /// that the mutator's invalidation walk is on its way to replacing, and a replaced state is
    /// never pinned by a later query.
    ///
    /// That is why the cyclic marker is recorded from the confirmed loop only. A stale terminal
    /// resolves the wrong services for a moment and then converges; a stale marker raises, which
    /// is not a value the caller can tell apart from a real cycle.
    /// </summary>
    private static void CacheResolvedTerminal(List<DelegationHop> path, InterceptorSubjectContext resolvedTerminal, int startIndex)
    {
        for (var index = startIndex; index < path.Count; index++)
        {
            path[index].State.SetResolvedTerminalIfAbsent(resolvedTerminal);
        }
    }

    /// <summary>
    /// Reports whether every context on the candidate loop still holds the state the walk pinned.
    /// A state is never installed twice, so one still in place has been in place since it was
    /// pinned, and every edge of the loop therefore existed at the same moment. That is a cycle.
    /// Comparing states rather than the fallback contexts they point at is what makes this exact:
    /// a walk reads each edge at its own time, so a sequence of rewirings that is acyclic
    /// throughout can otherwise produce a repeat that never was a cycle.
    ///
    /// <paramref name="loopStart"/> reports where the loop begins, because the run of contexts
    /// ahead of it is deliberately not re-read and therefore proves nothing, see
    /// <see cref="CacheResolvedTerminal"/>.
    /// </summary>
    private static bool DelegationLoopStillClosed(List<DelegationHop> path, InterceptorSubjectContext repeated, out int loopStart)
    {
        var index = 0;
        while (index < path.Count && !ReferenceEquals(path[index].Context, repeated))
        {
            index++;
        }

        loopStart = index;

        for (; index < path.Count; index++)
        {
            var context = path[index].Context;
            if (!ReferenceEquals(Volatile.Read(ref context._state), path[index].State))
            {
                return false;
            }
        }

        return true;
    }

    private static InvalidOperationException CreateDelegationCycleException()
    {
        return new InvalidOperationException(
            "The fallback contexts form a delegation cycle, so no service can be resolved. A context " +
            "without own services and with exactly one fallback context resolves everything through " +
            "that fallback context, and following those references leads back to a context already " +
            "visited. Break the cycle by removing one of the fallback context registrations or by " +
            "registering a service on one of the contexts on it.");
    }

    /// <summary>One context on the delegation walk and the state it was pinned on.</summary>
    private readonly struct DelegationHop(InterceptorSubjectContext context, ContextState state)
    {
        internal readonly InterceptorSubjectContext Context = context;

        internal readonly ContextState State = state;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadFunc<TProperty> GetReadInterceptorFunction<TProperty>(ContextState state)
    {
        var cached = state.TryGetReadFunction(PropertyTypeIndex<TProperty>.Value);
        if (cached is not null)
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
        state.SetReadFunction(PropertyTypeIndex<TProperty>.Value, function);
        return function;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private WriteAction<TProperty> GetWriteInterceptorFunction<TProperty>(ContextState state)
    {
        var cached = state.TryGetWriteFunction(PropertyTypeIndex<TProperty>.Value);
        if (cached is not null)
        {
            return (WriteAction<TProperty>)cached;
        }

        return CreateWriteInterceptorFunction<TProperty>(state);
    }

    private WriteAction<TProperty> CreateWriteInterceptorFunction<TProperty>(ContextState state)
    {
        var writeInterceptors = GetServicesFromState<IWriteInterceptor>(state);
        var action = WriteInterceptorFactory<TProperty>.Create(writeInterceptors);
        state.SetWriteFunction(PropertyTypeIndex<TProperty>.Value, action);
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
        return (InvokeFunc)state.GetOrSetMethodInvocationFunction(function);
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
            return CollectServices(typeof(TInterface), this, state, visited)
                .OfType<TInterface>()
                .ToImmutableArray();
        }
        finally
        {
            if (visited.Count > MaximumRetainedTraversalSize)
            {
                _serviceQueryVisited = null;
            }
            else
            {
                visited.Clear();
            }
        }
    }

    /// <summary>
    /// Collects the services of the given type from the pinned snapshot and from every context it
    /// reaches, walked with an explicit worklist rather than by recursion. The fallback graph is as
    /// deep as the subject graph that produced it, see <see cref="ResolveDelegationTarget"/>, so a
    /// recursive walk died on an uncatchable <see cref="StackOverflowException"/> on a graph that is
    /// otherwise legitimate.
    ///
    /// The shape of the recursive walk is preserved exactly, because the order of the result is
    /// observable: <see cref="ServiceOrderResolver.OrderByDependencies"/> keeps the input order
    /// among services that no ordering attribute separates. So the walk stays depth first and left
    /// to right, every context contributes its own services before those of its fallback contexts,
    /// and the collected services are made distinct and reordered once per context rather than once
    /// at the end.
    ///
    /// It terminates because a context is entered only when <c>visited</c> did not already hold it,
    /// so every context is pushed at most once and every iteration either pushes one or pops one.
    /// That bound is also what makes the walk safe on a cyclic fallback graph.
    /// </summary>
    private static List<object> CollectServices(
        Type type,
        InterceptorSubjectContext context,
        ContextState state,
        HashSet<InterceptorSubjectContext> visited)
    {
        // One buffer for the whole walk instead of one result per context: a context owns the tail
        // of the buffer from its own start index onwards, and reducing that tail in place leaves
        // everything the contexts below it already contributed untouched.
        var collected = new List<object>();
        if (!TryEnterContext(context, state, visited, out var enteredState))
        {
            return collected;
        }

        var frames = new List<ServiceWalkFrame>();
        var distinctServices = new HashSet<object>();
        PushFrame(frames, collected, type, enteredState);

        while (frames.Count != 0)
        {
            var frameIndex = frames.Count - 1;
            var frame = frames[frameIndex];
            var fallbackContexts = frame.State.FallbackContexts;

            var entered = false;
            while (frame.NextFallbackIndex < fallbackContexts.Length)
            {
                var fallbackContext = fallbackContexts[frame.NextFallbackIndex++];
                if (!TryEnterContext(fallbackContext, Volatile.Read(ref fallbackContext._state), visited, out var fallbackState))
                {
                    continue;
                }

                // The advanced cursor has to survive the push, the frame is a struct.
                frames[frameIndex] = frame;
                PushFrame(frames, collected, type, fallbackState);
                entered = true;
                break;
            }

            if (entered)
            {
                continue;
            }

            frames.RemoveAt(frameIndex);
            ReduceFrame(collected, frame.ResultStart, distinctServices);
        }

        return collected;
    }

    /// <summary>
    /// Marks the given context visited and follows its delegation chain, which is the one shape the
    /// walk collapses instead of giving it a frame: a context without own services and with exactly
    /// one fallback context contributes nothing of its own and resolves to exactly what its target
    /// resolves, so a whole chain shares the frame of the context it ends on. Returns <c>false</c>
    /// when the chain runs into a context that was already visited, which contributes nothing.
    ///
    /// It terminates because every hop adds a context to <c>visited</c> and it stops as soon as one
    /// is already in it, so it takes at most one hop per context in the graph.
    ///
    /// Every hop re-reads the state of the context it moves to and uses it for topology only, which
    /// is load bearing rather than incidental. See <see cref="ResolveDelegationChain"/> for why no
    /// walk may trust what a context further down recorded about the end of its chain.
    /// </summary>
    private static bool TryEnterContext(
        InterceptorSubjectContext context,
        ContextState state,
        HashSet<InterceptorSubjectContext> visited,
        out ContextState enteredState)
    {
        enteredState = state;

        while (visited.Add(context))
        {
            var delegationTarget = enteredState.DelegationTarget;
            if (delegationTarget is null)
            {
                return true;
            }


            context = delegationTarget;
            enteredState = Volatile.Read(ref delegationTarget._state);
        }

        return false;
    }

    /// <summary>
    /// Opens the buffer region of a context and appends its own matching services to it, which the
    /// recursive walk placed ahead of everything its fallback contexts contribute.
    /// </summary>
    private static void PushFrame(List<ServiceWalkFrame> frames, List<object> collected, Type type, ContextState state)
    {
        var resultStart = collected.Count;

        var services = state.Services;
        for (var index = 0; index < services.Length; index++)
        {
            var service = services[index];
            if (type.IsInstanceOfType(service))
            {
                collected.Add(service);
            }
        }

        frames.Add(new ServiceWalkFrame(state, resultStart));
    }

    /// <summary>
    /// Turns the buffer region that a context and its fallback contexts filled into the result of
    /// that context: duplicates dropped keeping the first occurrence, then reordered by the ordering
    /// attributes. That is what the recursive walk did per context with Distinct() and
    /// OrderByDependencies, and doing it per context rather than once at the end is what the order
    /// of the result depends on.
    /// </summary>
    private static void ReduceFrame(List<object> collected, int resultStart, HashSet<object> distinctServices)
    {
        // Compacted in place so that the duplicate removal needs no second buffer. The set uses the
        // same default comparer that Distinct() used, so it keeps the same occurrence of a service.
        distinctServices.Clear();
        var writeIndex = resultStart;
        for (var readIndex = resultStart; readIndex < collected.Count; readIndex++)
        {
            var service = collected[readIndex];
            if (distinctServices.Add(service))
            {
                collected[writeIndex++] = service;
            }
        }

        collected.RemoveRange(writeIndex, collected.Count - writeIndex);

        var count = collected.Count - resultStart;
        if (count == 0)
        {
            // OrderByDependencies returns an empty result for an empty input and does nothing else,
            // so what is skipped here is the copy out of and back into the buffer, not the call.
            return;
        }

        var services = new object[count];
        collected.CopyTo(resultStart, services, 0, count);

        // OrderByDependencies returns a permutation of what it was given, same length, so the
        // result goes straight back over the region it was taken from.
        var ordered = ServiceOrderResolver.OrderByDependencies(services);
        for (var index = 0; index < ordered.Length; index++)
        {
            collected[resultStart + index] = ordered[index];
        }
    }

    /// <summary>
    /// One context on the walk stack: the snapshot it was pinned on, the index at which its region
    /// of the shared buffer begins, and the cursor over its fallback contexts.
    /// </summary>
    private struct ServiceWalkFrame
    {
        internal readonly ContextState State;
        internal readonly int ResultStart;
        internal int NextFallbackIndex;

        internal ServiceWalkFrame(ContextState state, int resultStart)
        {
            State = state;
            ResultStart = resultStart;
            NextFallbackIndex = 0;
        }
    }

    /// <summary>
    /// R2: mutators publish under <see cref="_mutationLock"/>, no CAS loop. Mutators serialize on
    /// the lock, so none can lose another mutator's topology. The only lock-free writer is the
    /// invalidation CAS, which never changes topology; the state published here carries fresh
    /// caches, so overwriting a concurrent invalidation preserves its intent.
    ///
    /// The publish is an interlocked exchange rather than a volatile write because the publisher
    /// then reads the using sets and other contexts' states to drive the invalidation walk, and
    /// those reads must not be reordered before it. Release semantics constrains only the accesses
    /// ahead of the store, so it does not order this store against the later load, and store
    /// followed by load is reordered under every mainstream memory model, x86-64 TSO included,
    /// where the store sits in the store buffer while the load is already satisfied. A volatile
    /// write plus Monitor.Exit is therefore not guaranteed to help on any architecture, whatever
    /// fence a given runtime happens to emit for either. The consequence would be an invalidation
    /// skipped against a stale using set while the current state keeps accumulating cache fills
    /// computed from pre-mutation topology, leaving a compiled chain permanently missing an
    /// interceptor. The full fence closes that without touching the query path.
    /// </summary>
    private void PublishState(ContextState state)
    {
        Interlocked.Exchange(ref _state, state);
    }

    /// <summary>
    /// R3: one unconditional CAS attempt. No early-out when caches look absent, because a reader
    /// may be lazily creating a cache concurrently and skipping would let its insert survive the
    /// change. No retry on failure either, and that needs more than the competing write also being
    /// cache-free at publication, since it starts accepting fills immediately afterwards: a
    /// competing state can only win this CAS by being installed after the read above, which is
    /// fenced after the mutation was published, so every fill into it is computed from reads that
    /// see the mutation. That argument is what rests on the publish being interlocked rather than a
    /// release store, see PublishState.
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
            // Keyed on what the visited set grew to, not on the worklist: the using graph of a deep
            // chain queues one context per pop, so the worklist never grows while the visited set
            // takes an entry per level.
            if (visited.Count > MaximumRetainedTraversalSize)
            {
                _invalidationVisited = null;
                _invalidationPending = null;
            }
            else
            {
                visited.Clear();
                pending.Clear();
            }
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
        // computation lands in a state that no later query pins.
        private ConcurrentDictionary<Type, object>? _serviceCache; // stores ImmutableArray<T> boxed
        private Delegate? _methodInvocationFunction;

        // Indexed by PropertyTypeIndex, grown by replacing the array. Only a context a chain ends
        // on ever fills these, because everything above it resolves to that context, so a graph of
        // delegating subjects has as many arrays as it has contexts that answer.
        private Delegate?[]? _readFunctions;
        private Delegate?[]? _writeFunctions;

        // The context this state's delegation chain ends on, or CyclicDelegationMarker when that
        // chain runs in a circle and nothing resolves. Null until the chain is first walked. A
        // context and never a state, because the state of a context is replaced whenever anything
        // below it changes, so a cached state would keep serving the caches of an abandoned one.
        private InterceptorSubjectContext? _resolvedTerminal;

        internal ContextState(ImmutableArray<object> services, ImmutableArray<InterceptorSubjectContext> fallbackContexts)
        {
            Services = services;
            FallbackContexts = fallbackContexts;
            DelegationTarget = services.IsEmpty && fallbackContexts.Length == 1 ? fallbackContexts[0] : null;
        }

        internal bool IsEmpty => Services.IsEmpty && FallbackContexts.IsEmpty;

        internal InterceptorSubjectContext? ResolvedTerminal
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Volatile.Read(ref _resolvedTerminal);
        }

        internal void SetResolvedTerminalIfAbsent(InterceptorSubjectContext resolvedTerminal)
        {
            Interlocked.CompareExchange(ref _resolvedTerminal, resolvedTerminal, null);
        }

        internal Delegate? MethodInvocationFunction
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Volatile.Read(ref _methodInvocationFunction);
        }

        /// <summary>
        /// Always allocates, and must keep doing so. Returning this instance when it happens to
        /// carry no caches would make the invalidation CAS a no-op, so a recorded chain end would
        /// survive the change that invalidated it. It would also break the cycle confirmation,
        /// which proves a loop existed at one instant from a state object still being installed,
        /// and can only do that because a state is installed exactly once.
        /// </summary>
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
        internal Delegate? TryGetReadFunction(int propertyTypeIndex)
        {
            return TryGetFunction(ref _readFunctions, propertyTypeIndex);
        }

        internal void SetReadFunction(int propertyTypeIndex, Delegate function)
        {
            SetFunction(ref _readFunctions, propertyTypeIndex, function);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Delegate? TryGetWriteFunction(int propertyTypeIndex)
        {
            return TryGetFunction(ref _writeFunctions, propertyTypeIndex);
        }

        internal void SetWriteFunction(int propertyTypeIndex, Delegate function)
        {
            SetFunction(ref _writeFunctions, propertyTypeIndex, function);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Delegate? TryGetFunction(ref Delegate?[]? functions, int propertyTypeIndex)
        {
            // The element is intentionally read plainly to avoid the array element address helper
            // on the hot path. Modern .NET guarantees atomic managed-reference reads and safe
            // publication for state reached through an object reference without an acquiring read.
            // A concurrent fill can therefore only be missed, costing this caller one rebuild; it
            // cannot expose a partial delegate.
            var current = Volatile.Read(ref functions);
            return current is not null && propertyTypeIndex < current.Length
                ? current[propertyTypeIndex]
                : null;
        }

        /// <summary>
        /// Stores a compiled chain, growing the array when the index is beyond it. A store lost to
        /// a concurrent growth costs the next caller one recompilation, which is what a caller that
        /// loses the race already does: it invokes the chain it built rather than the one that won.
        /// </summary>
        private static void SetFunction(ref Delegate?[]? functions, int propertyTypeIndex, Delegate function)
        {
            while (true)
            {
                var current = Volatile.Read(ref functions);
                if (current is not null && propertyTypeIndex < current.Length)
                {
                    Interlocked.CompareExchange(ref current[propertyTypeIndex], function, null);
                    return;
                }

                // Doubled rather than sized to the index: filling the indices of a process with
                // many property types one at a time would otherwise reallocate once per type.
                var grown = new Delegate?[Math.Max(propertyTypeIndex + 1, (current?.Length ?? 0) * 2)];
                // CopyTo can likewise miss a slot filled concurrently. If this array wins the CAS,
                // the lost cache entry is rebuilt on its next use; copied references remain atomic
                // and are safely published when the grown array itself is installed below.
                current?.CopyTo(grown, 0);
                grown[propertyTypeIndex] = function;

                if (ReferenceEquals(Interlocked.CompareExchange(ref functions, grown, current), current))
                {
                    return;
                }
            }
        }

        internal Delegate GetOrSetMethodInvocationFunction(Delegate function)
        {
            return Interlocked.CompareExchange(ref _methodInvocationFunction, function, null) ?? function;
        }
    }
}
