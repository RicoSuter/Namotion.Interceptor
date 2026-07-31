using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Cache;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Ordering;

namespace Namotion.Interceptor;

public class InterceptorSubjectContext : IInterceptorSubjectContext
{
    // Lock ordering: _lock → UsedByContextsLock (never reverse).
    //
    // Across contexts, _lock may only be nested downwards, meaning a context may hold its own
    // _lock while taking the _lock of one of its fallback contexts (that is what a service query
    // does while walking the fallback chain). It must never be nested upwards into a using
    // context, because the two directions would then form a cycle. Practically that means
    // OnContextChanged, which walks _usedByContexts, is always invoked after _lock is released.
    // The remaining theoretical exposure is a fallback graph that contains a cycle, where the
    // downward direction is no longer a partial order.
    //
    // TODO(perf): Static lock simplifies cross-instance ordering but may contend under many independent trees.
    private static readonly object UsedByContextsLock = new();

    [ThreadStatic]
    private static HashSet<InterceptorSubjectContext>? _contextChangeVisited;

    [ThreadStatic]
    private static HashSet<InterceptorSubjectContext>? _serviceQueryVisited;
    
    private readonly object _lock = new();

    private ConcurrentDictionary<Type, Delegate>? _readInterceptorFunction;
    private ConcurrentDictionary<Type, Delegate>? _writeInterceptorFunction;
    private ConcurrentDictionary<Type, object>? _serviceCache; // stores ImmutableArray<T> boxed
    private Delegate? _methodInvocationFunction;

    private readonly HashSet<object> _services = []; // TODO(perf): Keep null initially?
    private readonly HashSet<InterceptorSubjectContext> _usedByContexts = [];
    private readonly HashSet<InterceptorSubjectContext> _fallbackContexts = [];

    private InterceptorSubjectContext? _noServicesSingleFallbackContext;

    public static InterceptorSubjectContext Create()
    {
        return new InterceptorSubjectContext();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ImmutableArray<TInterface> GetServices<TInterface>()
    {
        // When there is only a fallback context and no services then we do not
        // need to create an own cache and waste time creating and maintaining it.
        // We can just redirect the call to the fallback context.
        var noServicesSingleFallbackContext = _noServicesSingleFallbackContext;
        if (noServicesSingleFallbackContext is not null)
        {
            return noServicesSingleFallbackContext.GetServices<TInterface>();
        }

        EnsureInitialized();
        if (!_serviceCache!.TryGetValue(typeof(TInterface), out var services))
        {
            services = _serviceCache!.GetOrAdd(typeof(TInterface), _ =>
            {
                lock (_lock)
                {
                    return GetServicesWithoutCache<TInterface>().ToImmutableArray();
                }
            });
        }

        return (ImmutableArray<TInterface>)services;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureInitialized()
    {
        if (_serviceCache is null)
        {
            lock (_lock)
            {
                if (_serviceCache is null)
                {
                    _readInterceptorFunction = new ConcurrentDictionary<Type, Delegate>();
                    _writeInterceptorFunction = new ConcurrentDictionary<Type, Delegate>();

                    Volatile.Write(ref _serviceCache, new ConcurrentDictionary<Type, object>());
                }
            }
        }
    }

    public virtual bool AddFallbackContext(IInterceptorSubjectContext context)
    {
        var contextImpl = (InterceptorSubjectContext)context;
        bool requiresInvalidation;

        lock (_lock)
        {
            if (!_fallbackContexts.Add(contextImpl))
            {
                return false;
            }

            bool isUsedByOtherContexts;
            lock (UsedByContextsLock)
            {
                contextImpl._usedByContexts.Add(this);
                isUsedByOtherContexts = _usedByContexts.Count != 0;
            }

            // Fast path: first fallback on a fresh context (no services, no caches) that no other
            // context resolves through. There is nothing to invalidate, so only the delegation
            // field has to be set. The used-by check is required because OnContextChanged is also
            // the only thing that invalidates the contexts above, which would otherwise keep a
            // compiled chain that never sees the newly attached fallback.
            requiresInvalidation =
                isUsedByOtherContexts ||
                _serviceCache is not null ||
                _services.Count != 0 ||
                _fallbackContexts.Count != 1;

            if (!requiresInvalidation)
            {
                _noServicesSingleFallbackContext = contextImpl;
            }
        }

        if (requiresInvalidation)
        {
            OnContextChanged();
        }

        return true;
    }

    protected bool HasFallbackContext(IInterceptorSubjectContext context)
    {
        lock (_lock)
            return _fallbackContexts.Contains(context);
    }

    public virtual bool RemoveFallbackContext(IInterceptorSubjectContext context)
    {
        var contextImpl = (InterceptorSubjectContext)context;
        lock (_lock)
        {
            if (!_fallbackContexts.Remove(contextImpl))
            {
                return false;
            }

            lock (UsedByContextsLock) { contextImpl._usedByContexts.Remove(this); }
        }

        OnContextChanged();
        return true;
    }

    public bool TryAddService<TService>(Func<TService> factory, Func<TService, bool> exists)
    {
        lock (_lock)
        {
            // The lookup walks downwards into the fallback contexts and keeps the check and the
            // add atomic against concurrent mutations of this context, as before.
            if (GetServicesWithoutCache<TService>().Any(exists))
            {
                return false;
            }

            _services.Add(factory()!);
        }

        OnContextChanged();
        return true;
    }

    public void AddService<TService>(TService service)
    {
        lock (_lock)
        {
            _services.Add(service!);
        }

        OnContextChanged();
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
        var noServicesSingleFallbackContext = _noServicesSingleFallbackContext;
        if (noServicesSingleFallbackContext is not null)
        {
            return noServicesSingleFallbackContext.ExecuteInterceptedRead(ref context, readValue);
        }

        EnsureInitialized();
        var func = GetReadInterceptorFunction<TProperty>();
        return func(ref context, readValue);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ExecuteInterceptedWrite<TProperty>(ref PropertyWriteContext<TProperty> context, Action<IInterceptorSubject, TProperty> writeValue)
    {
        var noServicesSingleFallbackContext = _noServicesSingleFallbackContext;
        if (noServicesSingleFallbackContext is not null)
        {
            noServicesSingleFallbackContext.ExecuteInterceptedWrite(ref context, writeValue);
            return;
        }

        EnsureInitialized();
        var action = GetWriteInterceptorFunction<TProperty>();
        action(ref context, writeValue);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal object? ExecuteInterceptedInvoke(ref MethodInvocationContext context, Func<IInterceptorSubject, object?[], object?> invokeMethod)
    {
        var noServicesSingleFallbackContext = _noServicesSingleFallbackContext;
        if (noServicesSingleFallbackContext is not null)
        {
            return noServicesSingleFallbackContext.ExecuteInterceptedInvoke(ref context, invokeMethod);
        }

        EnsureInitialized();
        var func = GetMethodInvocationFunction();
        return func(ref context, invokeMethod);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadFunc<TProperty> GetReadInterceptorFunction<TProperty>()
    {
        if (_readInterceptorFunction!.TryGetValue(typeof(TProperty), out var cached))
        {
            return (ReadFunc<TProperty>)cached;
        }

        var readInterceptors = GetServices<IReadInterceptor>();
        var func = ReadInterceptorFactory<TProperty>.Create(readInterceptors);
        _readInterceptorFunction.TryAdd(typeof(TProperty), func);
        return func;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private WriteAction<TProperty> GetWriteInterceptorFunction<TProperty>()
    {
        if (_writeInterceptorFunction!.TryGetValue(typeof(TProperty), out var cached))
        {
            return (WriteAction<TProperty>)cached;
        }

        var writeInterceptors = GetServices<IWriteInterceptor>();
        var action = WriteInterceptorFactory<TProperty>.Create(writeInterceptors);
        _writeInterceptorFunction.TryAdd(typeof(TProperty), action);
        return action;
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private InvokeFunc GetMethodInvocationFunction()
    {
        if (_methodInvocationFunction is not null)
        {
            return (InvokeFunc)_methodInvocationFunction;
        }

        lock (_lock)
        {
            if (_methodInvocationFunction is not null)
            {
                return (InvokeFunc)_methodInvocationFunction;
            }

            var methodInterceptors = GetServices<IMethodInterceptor>();
            var func = MethodInvocationFactory.Create(methodInterceptors);
            _methodInvocationFunction = func;
            return func;
        }
    }

    private TInterface[] GetServicesWithoutCache<TInterface>()
    {
        // Fast path: single fallback, no local services - delegate to cached GetServices
        var singleFallback = _noServicesSingleFallbackContext;
        if (singleFallback is not null)
        {
            return [.. singleFallback.GetServices<TInterface>()];
        }

        // Fast path: no services and no fallbacks - return empty (common for fresh contexts)
        if (_services.Count == 0 && _fallbackContexts.Count == 0)
        {
            return [];
        }

        var visited = _serviceQueryVisited ??= [];
        try
        {
            return GetServicesWithoutCache(typeof(TInterface), visited)
                .OfType<TInterface>()
                .ToArray();
        }
        finally
        {
            visited.Clear();
        }
    }

    private IEnumerable<object> GetServicesWithoutCache(Type type, HashSet<InterceptorSubjectContext> visited)
    {
        if (!visited.Add(this))
        {
            return [];
        }

        InterceptorSubjectContext? delegateTo = null;
        InterceptorSubjectContext[] fallbacks = [];
        object[] localServices = [];

        lock (_lock)
        {
            // Fast path: no local services, single fallback - just delegate
            if (_services.Count == 0 && _fallbackContexts.Count == 1)
            {
                delegateTo = _fallbackContexts.First();
            }
            else
            {
                fallbacks = [.. _fallbackContexts];
                localServices = _services.Where(type.IsInstanceOfType).ToArray();
            }
        }

        // Recursive calls OUTSIDE the lock to prevent deadlock
        if (delegateTo is not null)
        {
            return delegateTo.GetServicesWithoutCache(type, visited);
        }

        var services = localServices
            .Concat(fallbacks.SelectMany(c => c.GetServicesWithoutCache(type, visited)))
            .Distinct()
            .ToArray();

        return ServiceOrderResolver.OrderByDependencies(services);
    }

    /// <summary>
    /// Invalidates the compiled chains of this context and of every context that resolves through
    /// it. Must be called without <see cref="_lock"/> held, see the lock ordering note at the top
    /// of the class: this walks upwards into the using contexts, while a service query walks
    /// downwards into the fallback contexts, and holding _lock across the upward walk lets the two
    /// directions deadlock.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void OnContextChanged()
    {
        var visited = _contextChangeVisited ??= new HashSet<InterceptorSubjectContext>();
        try
        {
            OnContextChanged(visited);
        }
        finally
        {
            visited.Clear();
        }
    }

    private void OnContextChanged(HashSet<InterceptorSubjectContext> visited)
    {
        if (!visited.Add(this))
        {
            return;
        }

        _serviceCache?.Clear();
        _readInterceptorFunction?.Clear();
        _writeInterceptorFunction?.Clear();
        _methodInvocationFunction = null;

        InterceptorSubjectContext? singleParent = null;
        InterceptorSubjectContext[]? parents = null;
        lock (_lock)
        {
            _noServicesSingleFallbackContext = _services.Count == 0 && _fallbackContexts.Count == 1
                ? _fallbackContexts.First() : null;

            // Avoid array allocation for common cases (0 or 1 parent)
            lock (UsedByContextsLock)
            {
                var usedByCount = _usedByContexts.Count;
                if (usedByCount == 1)
                {
                    singleParent = _usedByContexts.First();
                }
                else if (usedByCount > 1)
                {
                    parents = [.. _usedByContexts];
                }
            }
        }

        if (singleParent is not null)
        {
            singleParent.OnContextChanged(visited);
        }
        else if (parents is not null)
        {
            foreach (var parent in parents)
            {
                parent.OnContextChanged(visited);
            }
        }
    }
}
