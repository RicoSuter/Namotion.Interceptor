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
    // published atomically. Queries take no locks (R1): a query pins one snapshot with a single
    // volatile read and walks other contexts' snapshots the same way, so the downward service
    // walk and the upward invalidation walk cannot form a lock cycle, including in cyclic
    // fallback graphs.
    //
    // Lock order: _mutationLock -> UsedByContextsLock, never reverse. UsedByContextsLock
    // critical sections only touch a _usedByContexts set and never call into another context.
    //
    // TODO(perf): Static lock simplifies cross-instance ordering but may contend under many independent trees.
    private static readonly object UsedByContextsLock = new();

    [ThreadStatic]
    private static HashSet<InterceptorSubjectContext>? _contextChangeVisited;

    [ThreadStatic]
    private static HashSet<InterceptorSubjectContext>? _serviceQueryVisited;

    // Written via Volatile.Write and Interlocked.CompareExchange instead of being declared
    // volatile, which would raise CS0420 when passed by ref to Interlocked under
    // warnings-as-errors. Every context constructs its own initial state because caches live on
    // the state: one shared empty instance would let unrelated contexts contaminate each other's
    // caches.
    private ContextState _state = new(ImmutableArray<object>.Empty, ImmutableArray<InterceptorSubjectContext>.Empty);

    // Serializes mutators; never held on a query path.
    private readonly object _mutationLock = new();

    // Contexts that resolve through this context, lazily allocated because most contexts are
    // never used as a fallback.
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
        if (delegationTarget is not null)
        {
            // Delegation chains are one or two hops in practice. A pure delegation cycle (every
            // context empty) would recurse forever, which matches the pre-redesign behavior.
            return delegationTarget.GetServices<TInterface>();
        }

        return GetServicesFromState<TInterface>(state);
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
            lock (UsedByContextsLock)
            {
                (contextImpl._usedByContexts ??= []).Add(this);
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
            lock (UsedByContextsLock)
            {
                contextImpl._usedByContexts?.Remove(this);
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
            // into the same context is its own responsibility.
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
        if (delegationTarget is not null)
        {
            return delegationTarget.ExecuteInterceptedRead(ref context, readValue);
        }

        var function = GetReadInterceptorFunction<TProperty>(state);
        return function(ref context, readValue);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ExecuteInterceptedWrite<TProperty>(ref PropertyWriteContext<TProperty> context, Action<IInterceptorSubject, TProperty> writeValue)
    {
        var state = Volatile.Read(ref _state);
        var delegationTarget = state.DelegationTarget;
        if (delegationTarget is not null)
        {
            delegationTarget.ExecuteInterceptedWrite(ref context, writeValue);
            return;
        }

        var action = GetWriteInterceptorFunction<TProperty>(state);
        action(ref context, writeValue);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal object? ExecuteInterceptedInvoke(ref MethodInvocationContext context, Func<IInterceptorSubject, object?[], object?> invokeMethod)
    {
        var state = Volatile.Read(ref _state);
        var delegationTarget = state.DelegationTarget;
        if (delegationTarget is not null)
        {
            return delegationTarget.ExecuteInterceptedInvoke(ref context, invokeMethod);
        }

        var function = GetMethodInvocationFunction(state);
        return function(ref context, invokeMethod);
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
    /// R2: mutators publish with one volatile write under <see cref="_mutationLock"/>, no CAS
    /// loop. Mutators serialize on the lock, so none can lose another mutator's topology. The
    /// only lock-free writer is the invalidation CAS, which never changes topology; the state
    /// published here carries fresh caches, so overwriting a concurrent invalidation preserves
    /// its intent.
    /// </summary>
    private void PublishState(ContextState state)
    {
        Volatile.Write(ref _state, state);
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

    private void InvalidateUsingContexts()
    {
        var visited = _contextChangeVisited ??= [];
        try
        {
            // Self needs no invalidation here: the publish preceding this call already swapped
            // in a cache-free state.
            visited.Add(this);
            InvalidateUsingContexts(visited);
        }
        finally
        {
            visited.Clear();
        }
    }

    private void InvalidateUsingContexts(HashSet<InterceptorSubjectContext> visited)
    {
        // Snapshot under the lock, invalidate after release: calling into another context while
        // holding UsedByContextsLock is forbidden by the lock order. The 0/1/many shapes avoid
        // an array allocation in the common cases.
        InterceptorSubjectContext? singleUsingContext = null;
        InterceptorSubjectContext[]? usingContexts = null;

        lock (UsedByContextsLock)
        {
            var usedByContexts = _usedByContexts;
            if (usedByContexts is not null && usedByContexts.Count != 0)
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
                else
                {
                    usingContexts = [.. usedByContexts];
                }
            }
        }

        if (singleUsingContext is not null)
        {
            InvalidateUsingContext(singleUsingContext, visited);
        }
        else if (usingContexts is not null)
        {
            foreach (var usingContext in usingContexts)
            {
                InvalidateUsingContext(usingContext, visited);
            }
        }
    }

    private static void InvalidateUsingContext(InterceptorSubjectContext usingContext, HashSet<InterceptorSubjectContext> visited)
    {
        if (!visited.Add(usingContext))
        {
            return;
        }

        usingContext.InvalidateState();
        usingContext.InvalidateUsingContexts(visited);
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
