using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Cache;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Ordering;

namespace Namotion.Interceptor;

public class InterceptorSubjectContext : IInterceptorSubjectContext
{
    // All topology (services, fallback contexts) and everything derived from it (delegation
    // target, caches, compiled chains) lives in one immutable snapshot per context, published
    // atomically.
    //
    // R1: queries take no context lock. A query pins one snapshot with a single volatile read and
    // walks other contexts' snapshots the same way, so the downward service walk and the upward
    // invalidation walk cannot form a lock cycle, including in cyclic fallback graphs.
    //
    // Lock order: _mutationLock -> a _usedByContexts set lock, never reverse. A set lock is a leaf,
    // touching only that set and calling into no other context, so two contexts registering into
    // each other cannot deadlock. No path takes a second _mutationLock; the only way to nest them
    // is a TryAddService factory or exists predicate that mutates a different context, which the
    // contract forbids for that reason.

    private const int MaximumRetainedTraversalSize = 1024;

    // Declared before the marker so that a future change to marker construction cannot observe
    // its zero-initialized value.
    private static int _lastPropertyTypeIndex = -1;

    // Recorded on a state whose chain was proven cyclic, so a context on the loop pays one walk
    // per state rather than one per query. A context merely leading into a loop records nothing
    // and re-walks every query, which is the price of never raising from an unverified record.
    // A context rather than a marker object so that the slot can be typed: this class is not
    // sealed, so a type test on an object slot compiles to a runtime helper call on every
    // intercepted access.
    private static readonly InterceptorSubjectContext CyclicDelegationMarker = CreateCyclicDelegationMarker();

    /// <summary>
    /// A dense index per intercepted property type, so a compiled chain is found by indexing an
    /// array instead of hashing a <see cref="Type"/>. Handed out process wide to keep the lookup a
    /// plain array read; the cost is that an array is as long as the largest index its context has
    /// seen rather than the number of types it uses.
    /// </summary>
    private static class PropertyTypeIndex<TProperty>
    {
        // ReSharper disable once StaticMemberInGenericType
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

    // Written via Interlocked rather than declared volatile, which would raise CS0420 when passed
    // by ref under warnings-as-errors. Every context builds its own initial state because caches
    // live on the state: one shared empty instance would let contexts contaminate each other.
    private ContextState _state = new(ImmutableArray<object>.Empty, ImmutableArray<InterceptorSubjectContext>.Empty, null);

    // Serializes mutators; never held on a query path. Reachable from InterceptorExecutor so that a
    // guard and the publish it protects are one critical section.
    private protected readonly object _mutationLock = new();

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

    /// <summary>
    /// Called inside the mutation critical section before a fallback edge is added, so a subclass
    /// can reject the call while holding the same lock that publishes the edge. Reading the state,
    /// releasing the lock and then calling the base would be check-then-act across a lock boundary.
    /// </summary>
    protected virtual void OnAddingFallbackContext(IInterceptorSubjectContext context)
    {
    }

    /// <summary>
    /// Called inside the mutation critical section before a fallback edge is removed. See
    /// <see cref="OnAddingFallbackContext"/>.
    /// </summary>
    protected virtual void OnRemovingFallbackContext(IInterceptorSubjectContext context)
    {
    }

    public virtual bool AddFallbackContext(IInterceptorSubjectContext context)
    {
        var contextImpl = (InterceptorSubjectContext)context;

        lock (_mutationLock)
        {
            OnAddingFallbackContext(context);

            var state = Volatile.Read(ref _state);
            if (state.FallbackContexts.Contains(contextImpl))
            {
                return false;
            }

            RegisterUsedBy(contextImpl);
            PublishState(new ContextState(state.Services, state.FallbackContexts.Add(contextImpl), state.Parent));
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
        return RemoveFallbackContextCore(context, runGuard: true);
    }

    /// <summary>
    /// Removes an edge the library owns, bypassing the guard that rejects a consumer removing the
    /// attach edge. The public guard cannot distinguish the library's own cleanup from a consumer's
    /// call, and either answer would be wrong for the other.
    /// </summary>
    internal bool RemoveAttachEdge(IInterceptorSubjectContext context)
    {
        return RemoveFallbackContextCore(context, runGuard: false);
    }

    private bool RemoveFallbackContextCore(IInterceptorSubjectContext context, bool runGuard)
    {
        var contextImpl = (InterceptorSubjectContext)context;

        lock (_mutationLock)
        {
            if (runGuard)
            {
                OnRemovingFallbackContext(context);
            }

            var state = Volatile.Read(ref _state);
            var index = state.FallbackContexts.IndexOf(contextImpl);
            if (index < 0)
            {
                return false;
            }

            var newState = new ContextState(state.Services, state.FallbackContexts.RemoveAt(index), state.Parent);
            PublishState(newState);

            // R4: unregister only AFTER publishing so the using set stays a superset for the whole
            // transition, and only when no remaining edge targets the same context.
            UnregisterUsedByIfUnreferenced(newState, contextImpl);
        }

        InvalidateUsingContexts();
        return true;
    }

    /// <summary>Whether this context currently inherits from a parent subject's context.</summary>
    internal bool HasParentContext => Volatile.Read(ref _state).Parent is not null;

    /// <summary>
    /// Whether this context inherits from a parent other than the given one. Two co-resolved
    /// <c>ContextInheritanceHandler</c> instances both publish the same link for the same reference,
    /// which is idempotent and not the violation the link assertion is looking for.
    /// </summary>
    // Feeds a Debug.Assert only, so it has no caller in a Release build.
    internal bool HasOtherParentContext(IInterceptorSubjectContext parent)
    {
        var current = Volatile.Read(ref _state).Parent;
        return current is not null && !ReferenceEquals(current, parent);
    }

    /// <summary>
    /// Publishes the inherited parent context. The single write site is
    /// <c>ContextInheritanceHandler</c> at reference count one, and the cycle argument in
    /// docs/superpowers/specs/2026-08-04-context-inheritance-design.md section 4 depends on it
    /// staying single and on three guards holding together: the attach edge surviving while the
    /// subject is referenced, RemoveFallbackContext rejecting the attach edge, and
    /// DetachFromContext rejecting a non-zero reference count. Relax any one and a root can become
    /// a pure delegator while holding a link, at which point this becomes cycle-capable.
    ///
    /// A fourth relaxation exists on the error path: ClearAttachContext removes the attach edge with
    /// no reference-count check, so a subject that is already a property child, is root-attached
    /// within the same graph and whose attach then throws is left parent-only. Closing a delegation
    /// cycle from there still takes a reference cycle in the subject graph, of any length: the
    /// mutual pair is one shape of it, not the requirement.
    /// </summary>
    internal bool TrySetParentContext(IInterceptorSubjectContext parent)
    {
        var parentImpl = (InterceptorSubjectContext)parent;

        lock (_mutationLock)
        {
            var state = Volatile.Read(ref _state);
            if (state.Parent is not null)
            {
                return false;
            }

            RegisterUsedBy(parentImpl);
            PublishState(new ContextState(state.Services, state.FallbackContexts, parentImpl));
        }

        InvalidateUsingContexts();
        return true;
    }

    internal bool TryClearParentContext()
    {
        lock (_mutationLock)
        {
            var state = Volatile.Read(ref _state);
            var parent = state.Parent;
            if (parent is null)
            {
                return false;
            }

            var newState = new ContextState(state.Services, state.FallbackContexts, null);
            PublishState(newState);
            UnregisterUsedByIfUnreferenced(newState, parent);
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

            // The factory may reenter this context (Monitor is reentrant) and publish, so re-read
            // the state to not lose it. Mutating a different context from here is forbidden, see
            // the lock order note at the top of the class.
            state = Volatile.Read(ref _state);
            PublishState(new ContextState(state.Services.Add(service!), state.FallbackContexts, state.Parent));
        }

        InvalidateUsingContexts();
        return true;
    }

    public void AddService<TService>(TService service)
    {
        lock (_mutationLock)
        {
            var state = Volatile.Read(ref _state);
            PublishState(new ContextState(state.Services.Add(service!), state.FallbackContexts, state.Parent));
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
    /// the state that context was pinned on. A context with no own service and exactly one fallback
    /// resolves everything through it, so the chain is as deep as the subject graph.
    ///
    /// Walked once per state rather than once per query, which is what makes depth free. The record
    /// holds the terminal CONTEXT and never its state: a context's state is replaced whenever
    /// anything below it changes, so the re-read below cannot be removed.
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

        var backoff = new SpinWait();

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

                // Only the entry's own record may be trusted. Everything below is read for topology
                // alone, the one thing an installed state always describes correctly. A record is a
                // claim about contexts further down, and because R4 keeps a using set a superset an
                // invalidation can arrive out of chain order; trusting such a record would cache a
                // resolution of a context the graph no longer reaches, on a state nothing
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
                    // Every hop that entered visited also entered path, and the break above fires
                    // only on a context already in visited, so the repeat is always on the path.
                    Debug.Assert(loopStart < path.Count, "The repeated context was not found on the walked path.");

                    // Only from the loop, never from the acyclic run leading into it: the
                    // confirmation re-reads the loop's states and nothing else, so a context ahead
                    // of it reaches the loop by an edge read earlier and possibly rewired since.
                    CacheResolvedTerminal(path, CyclicDelegationMarker, loopStart);
                    throw CreateDelegationCycleException();
                }

                // The loop came apart under the walk, so it was a rewiring and not a cycle. A real
                // cycle has no state to lose and confirms on the next pass. Backing off because
                // reaching here means a mutator is rewiring right now and nothing else bounds this
                // loop; only retries pay it.
                backoff.SpinOnce();
            }
        }
        finally
        {
            // Dropped rather than cleared past the threshold: Clear() keeps the capacity, so one
            // deep walk would hold an entry per level on this thread for the rest of the process.
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
    /// Records where the chain ends from <paramref name="startIndex"/> onwards, turning the next
    /// query on any of those contexts into a single hop. Written only to the objects the walk
    /// pinned, never to a re-read of a context's current state.
    ///
    /// The guarantee is quiescent, not instantaneous: the walk reads each edge at its own time, so
    /// the chain it records may never have existed all at once. It converges because any change
    /// below a context replaces that context's state, and a replaced state is never pinned again.
    ///
    /// The cyclic marker is therefore recorded from the confirmed loop only. A stale terminal
    /// resolves wrongly for a moment and converges; a stale marker raises, which a caller cannot
    /// tell from a real cycle. On the loop that raise is bounded by the invalidation walk and its
    /// callers were already raising anyway; a context leading into the loop has no such bound.
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
    /// Comparing states rather than the fallback contexts they point at is what makes this exact: a
    /// sequence of rewirings that is acyclic throughout can otherwise produce a repeat.
    ///
    /// <paramref name="loopStart"/> reports where the loop begins, because the run ahead of it is
    /// deliberately not re-read and proves nothing, see <see cref="CacheResolvedTerminal"/>.
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

        // A single slot, so returning the CAS winner is free. The read and write paths return what
        // they built instead, which is equivalent: racers compile from the same state's services,
        // so their chains differ only in identity.
        return (InvokeFunc)state.GetOrSetMethodInvocationFunction(function);
    }

    /// <summary>
    /// Resolves services from the given pinned snapshot, normally one whose delegation the caller
    /// already resolved. That is an expectation and not a precondition: the walk re-follows
    /// delegation from whatever state it is handed. The cache entry is computed from the same
    /// snapshot that owns the cache, so a topology change (which publishes a new state) can never
    /// receive a stale entry.
    /// </summary>
    private ImmutableArray<TInterface> GetServicesFromState<TInterface>(ContextState state)
    {
        // An empty context allocates no service cache. The compiled chain arrays are unaffected:
        // an empty state that answers intercepted access still fills those.
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
    /// Collects the services of the given type from the pinned snapshot and every context it
    /// reaches, with an explicit worklist rather than recursion: the fallback graph is as deep as
    /// the subject graph, so recursion died on an uncatchable <see cref="StackOverflowException"/>.
    ///
    /// The shape of the recursive walk is preserved exactly, because the result order is
    /// observable: <see cref="ServiceOrderResolver.OrderByDependencies"/> keeps input order among
    /// services no ordering attribute separates. So the walk stays depth first and left to right,
    /// each context contributes its own services first, and the result is made distinct and
    /// reordered once per context rather than once at the end.
    ///
    /// It terminates because a context is entered only when <c>visited</c> did not hold it, which
    /// is also what makes the walk safe on a cyclic graph.
    /// </summary>
    private static List<object> CollectServices(
        Type type,
        InterceptorSubjectContext context,
        ContextState state,
        HashSet<InterceptorSubjectContext> visited)
    {
        // One buffer for the whole walk instead of one result per context: a context owns the
        // buffer from its own start index on, and reducing that region leaves the rest untouched.
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
            var edgeCount = fallbackContexts.Length + (frame.State.Parent is not null ? 1 : 0);
            while (frame.NextFallbackIndex < edgeCount)
            {
                var edgeIndex = frame.NextFallbackIndex++;
                var nextContext = edgeIndex < fallbackContexts.Length
                    ? fallbackContexts[edgeIndex]
                    : frame.State.Parent!;

                if (!TryEnterContext(nextContext, Volatile.Read(ref nextContext._state), visited, out var nextState))
                {
                    continue;
                }

                // The advanced cursor has to survive the push, the frame is a struct.
                frames[frameIndex] = frame;
                PushFrame(frames, collected, type, nextState);
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
    /// Marks the context visited and follows its delegation chain, the one shape the walk collapses
    /// instead of giving it a frame: a pure delegator contributes nothing and resolves to exactly
    /// what its target does, so a whole chain shares the frame of the context it ends on. Returns
    /// <c>false</c> when the chain runs into an already visited context.
    ///
    /// It terminates because every hop adds to <c>visited</c> and stops as soon as one is already
    /// in it.
    ///
    /// Every hop re-reads the state it moves to and uses it for topology ONLY, which is load
    /// bearing: see <see cref="ResolveDelegationChain"/> for why no walk may trust what a context
    /// further down recorded about the end of its chain.
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
    /// Turns the buffer region a context and its fallbacks filled into that context's result:
    /// duplicates dropped keeping the first occurrence, then reordered by the ordering attributes.
    /// Per context rather than once at the end, which is what the result order depends on.
    /// </summary>
    private static void ReduceFrame(List<object> collected, int resultStart, HashSet<object> distinctServices)
    {
        // Compacted in place, so dedup needs no second buffer. Default comparer, matching the
        // Distinct() this replaced, so the same occurrence survives.
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
            // Skips the copy out of and back into the buffer, not any ordering work.
            return;
        }

        var services = new object[count];
        collected.CopyTo(resultStart, services, 0, count);

        // A permutation of the input, same length, so it goes straight back over the same region.
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
    /// R2: mutators publish under <see cref="_mutationLock"/>, no CAS loop, so none can lose
    /// another's topology. The only lock-free writer is the invalidation CAS, which never changes
    /// topology, and the state published here is cache-free, so overwriting one preserves its
    /// intent.
    ///
    /// Interlocked rather than a volatile write because the publisher then READS using sets and
    /// other contexts' states to drive the invalidation walk. Release semantics does not order a
    /// store against a later load, and store-then-load is reordered under every mainstream model,
    /// x86-64 included, so a volatile write plus Monitor.Exit would not be enough. Without the
    /// fence an invalidation could be skipped against a stale using set while the current state
    /// keeps accepting fills computed from pre-mutation topology.
    ///
    /// InvalidateState needs one step more: an atomic read-modify-write becomes visible to every
    /// thread when it executes, so a later read of another location cannot be satisfied from before
    /// it. Every target provides this; the CLI memory model does not state it, so it is an
    /// assumption rather than a guarantee.
    /// </summary>
    private void PublishState(ContextState state)
    {
        Interlocked.Exchange(ref _state, state);
    }

    /// <summary>
    /// R3: one unconditional CAS attempt. No early-out when caches look absent, because a reader
    /// may be creating one concurrently and skipping would let its insert survive the change. No
    /// retry either: a competing state can only win this CAS by being installed after the read
    /// above, which is fenced after the mutation was published, so every fill into it already sees
    /// the mutation. That rests on the publish being interlocked, see PublishState.
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
    /// R4: register into the target BEFORE publishing, so its using set is always a superset of the
    /// true using set. A missing entry leaves a compiled chain above permanently stale. An extra
    /// entry costs a spurious invalidation and lets the invalidation walk arrive out of chain order,
    /// which is why no walk may trust what a context further down recorded.
    /// </summary>
    private void RegisterUsedBy(InterceptorSubjectContext target)
    {
        var usedByContexts = target.GetOrCreateUsedByContexts();
        lock (usedByContexts)
        {
            usedByContexts.Add(this);
        }
    }

    /// <summary>
    /// Drops the reverse entry only when no remaining edge of this context targets
    /// <paramref name="target"/>. Two edge kinds make two edges to one target possible, and
    /// unregistering unconditionally would unregister the sole reverse entry while the other edge
    /// still resolves through it, so invalidation would never reach this context again and its
    /// compiled chain would silently keep an interceptor set the graph no longer has. That is
    /// #400's defect 6, unreachable on master where one edge kind plus dedup rules it out.
    /// </summary>
    private void UnregisterUsedByIfUnreferenced(ContextState state, InterceptorSubjectContext target)
    {
        if (state.FallbackContexts.Contains(target) || ReferenceEquals(state.Parent, target))
        {
            return;
        }

        var usedByContexts = Volatile.Read(ref target._usedByContexts);
        if (usedByContexts is null)
        {
            return;
        }

        lock (usedByContexts)
        {
            usedByContexts.Remove(this);
        }
    }

    /// <summary>
    /// Invalidates every context that resolves through this one, with an explicit worklist rather
    /// than recursion: the using graph is the fallback graph reversed and therefore as deep as the
    /// subject graph, where recursion died on an uncatchable <see cref="StackOverflowException"/>.
    ///
    /// It terminates because a context is queued only when <c>visited</c> did not hold it, which is
    /// also what makes the walk safe on a cyclic graph.
    ///
    /// No user code runs here, so the only exits other than completing are OutOfMemoryException and
    /// Thread.Interrupt; either leaves contexts not yet reached holding pre-mutation caches that
    /// nothing repairs, as the recursive walk this replaced also did.
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
                // Removing from the end costs no shifting. Order is not observable: invalidating
                // one context is an independent CAS that never reads another.
                var lastIndex = pending.Count - 1;
                var usingContext = pending[lastIndex];
                pending.RemoveAt(lastIndex);

                usingContext.InvalidateState();
                QueueUsingContexts(usingContext, visited, pending);
            }
        }
        finally
        {
            // Keyed on the visited set, not the worklist: a deep chain queues one context per pop,
            // so the worklist stays short while visited takes an entry per level.
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
        // Contexts never used as a fallback take no lock. The field is written once by a CAS
        // ordered before the registrant's own publish, so a racing registration is either visible
        // here or belongs to a context that has not published yet, whose own walk then covers
        // everything above it. This depends on the publish being a full fence, see PublishState.
        //
        // Emptiness is deliberately NOT checked outside the lock: HashSet.Count is a composite of
        // two independently mutated fields, so an unlocked read can compute a count that was never
        // true and skip a using context for good.
        var usedByContexts = Volatile.Read(ref context._usedByContexts);
        if (usedByContexts is null)
        {
            return;
        }

        // Snapshot under the set lock, queue after release: calling into another context while
        // holding a set lock is forbidden by the lock order. The 0/1/many shapes avoid an array.
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
        // Insertion order is kept and duplicate references tolerated: dedup moved from
        // registration into the service walk, which keeps the same occurrence under the same
        // comparer. It differs only in that the walk filters by the queried type first, so two
        // services that compare equal while only the later matches that type now resolve to it
        // and previously to nothing.
        internal readonly ImmutableArray<object> Services;
        internal readonly ImmutableArray<InterceptorSubjectContext> FallbackContexts;

        // The inherited context of a subject held by a parent property. A separate slot rather than
        // an entry in FallbackContexts because it is owned by the lifecycle system and must not be
        // reachable from RemoveFallbackContext. Resolution visits it last, so explicit composition
        // beats inheritance.
        internal readonly InterceptorSubjectContext? Parent;

        // Derived in the constructor from the three fields above, so no reader can ever observe
        // it disagreeing with them.
        internal readonly InterceptorSubjectContext? DelegationTarget;

        // Caches belong to the state that produced them, created lazily via CAS. A topology change
        // publishes a new state, so a late insert lands in a state no later query pins.
        private ConcurrentDictionary<Type, object>? _serviceCache; // stores ImmutableArray<T> boxed
        private Delegate? _methodInvocationFunction;

        // Indexed by PropertyTypeIndex, grown by replacing the array. Only a context a chain ends
        // on ever fills these, since everything above it resolves to that context.
        private Delegate?[]? _readFunctions;
        private Delegate?[]? _writeFunctions;

        // The context this state's chain ends on, or CyclicDelegationMarker when it runs in a
        // circle. Null until first walked. A context and never a state: a context's state is
        // replaced whenever anything below it changes, so a cached state would serve an abandoned
        // one's caches.
        private InterceptorSubjectContext? _resolvedTerminal;

        internal ContextState(
            ImmutableArray<object> services,
            ImmutableArray<InterceptorSubjectContext> fallbackContexts,
            InterceptorSubjectContext? parent)
        {
            Services = services;
            FallbackContexts = fallbackContexts;
            Parent = parent;

            // A context with no own service and exactly one outgoing edge resolves everything
            // through it, whichever kind that edge is. The parent-only case is the dominant
            // topology after this change, so it has to qualify or every child pays a full walk.
            DelegationTarget = services.IsEmpty
                ? fallbackContexts.Length == 1 && parent is null ? fallbackContexts[0]
                : fallbackContexts.IsEmpty && parent is not null ? parent
                : null
                : null;
        }

        // Parent counts: without it a parent-only state would be "empty" and resolve nothing,
        // and today that only works by accident because such a state has a DelegationTarget.
        internal bool IsEmpty => Services.IsEmpty && FallbackContexts.IsEmpty && Parent is null;

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
        /// Always allocates, and must keep doing so. Returning this instance when it carries no
        /// caches would make the invalidation CAS a no-op, so a recorded chain end would survive
        /// the change that invalidated it, and would break the cycle confirmation, which proves a
        /// loop existed at one instant from a state being installed exactly once.
        /// </summary>
        internal ContextState WithoutCaches()
        {
            return new ContextState(Services, FallbackContexts, Parent);
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
            // Read plainly to avoid the array element address helper on the hot path. Managed
            // reference reads are atomic and safely published, so a concurrent fill can only be
            // missed (costing one rebuild), never observed partially.
            var current = Volatile.Read(ref functions);
            return current is not null && propertyTypeIndex < current.Length
                ? current[propertyTypeIndex]
                : null;
        }

        /// <summary>
        /// Stores a compiled chain, growing the array when the index is beyond it. A store lost to
        /// a concurrent growth costs the next caller one recompilation, which is what a caller
        /// losing the race already does.
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

                // Doubled rather than sized to the index, which would reallocate once per property
                // type in a process that has many.
                var grown = new Delegate?[Math.Max(propertyTypeIndex + 1, (current?.Length ?? 0) * 2)];

                // CopyTo can miss a slot filled concurrently; that entry is rebuilt on next use.
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
