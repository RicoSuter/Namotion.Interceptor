using System.Collections;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Cache;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor;

public class InterceptorSubjectContext : IInterceptorSubjectContext
{
    // PERFORMANCE: Group hot-path fields together for cache-line locality
    // These fields are accessed on EVERY property read/write operation
    private ConcurrentDictionary<Type, Delegate>? _readInterceptorFunction;
    private ConcurrentDictionary<Type, Delegate>? _writeInterceptorFunction;
    private ConcurrentDictionary<Type, IEnumerable>? _serviceCache;

    // PERFORMANCE: Not volatile - we use proper locking instead of memory barriers
    // volatile would force memory barrier on every read, killing performance
    private Delegate? _methodInvocationFunction;
    private IInterceptorSubjectContext? _noServicesSingleFallbackContext;

    // PERFORMANCE: HashSet for collections modified only under lock(this)
    private readonly HashSet<object> _services = [];
    private readonly HashSet<InterceptorSubjectContext> _fallbackContexts = [];

    // PERFORMANCE: ConcurrentDictionary for _usedByContexts because it's modified from
    // "outside" when another context calls AddFallbackContext(this) - avoids nested locks
    private readonly ConcurrentDictionary<InterceptorSubjectContext, byte> _usedByContexts = [];

    public static InterceptorSubjectContext Create()
    {
        return new InterceptorSubjectContext();
    }

    /// <summary>
    /// HOT PATH: Called on every property access after interceptor functions are cached.
    /// Optimization priority: minimize branches, memory barriers, and allocations.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IEnumerable<TInterface> GetServices<TInterface>()
    {
        // PERFORMANCE: Fast-path delegation to single fallback context
        // This optimization avoids cache creation for contexts that only delegate
        // Load to local variable to avoid multiple volatile reads
        var noServicesSingleFallbackContext = _noServicesSingleFallbackContext;
        if (noServicesSingleFallbackContext is not null)
        {
            return noServicesSingleFallbackContext.GetServices<TInterface>();
        }

        EnsureInitialized();

        // PERFORMANCE: Fast path - check cache without entering GetOrAdd factory
        // TryGetValue is faster than GetOrAdd when cache hit rate is high
        if (!_serviceCache!.TryGetValue(typeof(TInterface), out var services))
        {
            // PERFORMANCE: Only lock when cache miss occurs (rare after warm-up)
            // Lock is inside GetOrAdd factory to ensure only one thread builds cache per type
            services = _serviceCache!.GetOrAdd(typeof(TInterface), _ =>
            {
                lock (this)
                {
                    // PERFORMANCE: ToArray() materializes LINQ chain immediately
                    // This prevents repeated enumeration and captures snapshot under lock
                    return GetServicesWithoutCache<TInterface>().ToArray();
                }
            });
        }

        return (TInterface[])services;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureInitialized()
    {
        // PERFORMANCE: Single null check for initialization
        // After initialization, this is just a cheap null check (no memory barrier)
        if (_serviceCache is null)
        {
            lock (this)
            {
                // PERFORMANCE: Double-check pattern to ensure single initialization
                if (_serviceCache is null)
                {
                    _serviceCache = new ConcurrentDictionary<Type, IEnumerable>();
                    _readInterceptorFunction = new ConcurrentDictionary<Type, Delegate>();
                    _writeInterceptorFunction = new ConcurrentDictionary<Type, Delegate>();
                }
            }
        }
    }

    public virtual bool AddFallbackContext(IInterceptorSubjectContext context)
    {
        var contextImpl = (InterceptorSubjectContext)context;
        lock (this)
        {
            if (_fallbackContexts.Add(contextImpl))
            {
                // No lock needed - _usedByContexts is ConcurrentDictionary
                contextImpl._usedByContexts.TryAdd(this, 0);
                OnContextChanged();
                return true;
            }

            return false;
        }
    }

    protected bool HasFallbackContext(IInterceptorSubjectContext context)
    {
        lock (this)
        {
            return context is InterceptorSubjectContext ctx && _fallbackContexts.Contains(ctx);
        }
    }

    public virtual bool RemoveFallbackContext(IInterceptorSubjectContext context)
    {
        var contextImpl = (InterceptorSubjectContext)context;
        lock (this)
        {
            if (_fallbackContexts.Remove(contextImpl))
            {
                // No lock needed - _usedByContexts is ConcurrentDictionary
                contextImpl._usedByContexts.TryRemove(this, out _);
                OnContextChanged();
                return true;
            }

            return false;
        }
    }

    public bool TryAddService<TService>(Func<TService> factory, Func<TService, bool> exists)
    {
        lock (this)
        {
            if (GetServicesWithoutCache<TService>().Any(exists))
            {
                return false;
            }

            _services.Add(factory()!);
            OnContextChanged();
        }

        return true;
    }

    public void AddService<TService>(TService service)
    {
        lock (this)
        {
            _services.Add(service!);
            OnContextChanged();
        }
    }

    public TInterface? TryGetService<TInterface>()
    {
        var services = (TInterface[])GetServices<TInterface>();
        var length = services.Length;
        return length switch
        {
            1 => services[0],
            0 => default,
            _ => throw new InvalidOperationException($"There must be exactly one service of type {typeof(TInterface).FullName}.")
        };
    }

    /// <summary>
    /// HOT PATH: Called on EVERY property read.
    /// Critical to inline and minimize overhead.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TProperty ExecuteInterceptedRead<TProperty>(ref PropertyReadContext context, Func<IInterceptorSubject, TProperty> readValue)
    {
        // PERFORMANCE: Fast-path delegation
        var noServicesSingleFallbackContext = _noServicesSingleFallbackContext;
        if (noServicesSingleFallbackContext is not null)
        {
            return noServicesSingleFallbackContext.ExecuteInterceptedRead(ref context, readValue);
        }

        EnsureInitialized();
        var func = GetReadInterceptorFunction<TProperty>();
        return func(ref context, readValue);
    }

    /// <summary>
    /// HOT PATH: Called on EVERY property write.
    /// Critical to inline and minimize overhead.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ExecuteInterceptedWrite<TProperty>(ref PropertyWriteContext<TProperty> context, Action<IInterceptorSubject, TProperty> writeValue)
    {
        // PERFORMANCE: Fast-path delegation
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
    public object? ExecuteInterceptedInvoke(ref MethodInvocationContext context, Func<IInterceptorSubject, object?[], object?> invokeMethod)
    {
        // PERFORMANCE: Fast-path delegation
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
        // PERFORMANCE: Fast path - check cache first
        if (_readInterceptorFunction!.TryGetValue(typeof(TProperty), out var cached))
        {
            return (ReadFunc<TProperty>)cached;
        }

        // PERFORMANCE: Cache miss - build and store interceptor chain
        // This happens once per property type, then cached forever
        var readInterceptors = GetServices<IReadInterceptor>();
        var func = ReadInterceptorFactory<TProperty>.Create(readInterceptors);
        _readInterceptorFunction.TryAdd(typeof(TProperty), func);
        return func;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private WriteAction<TProperty> GetWriteInterceptorFunction<TProperty>()
    {
        // PERFORMANCE: Fast path - check cache first
        if (_writeInterceptorFunction!.TryGetValue(typeof(TProperty), out var cached))
        {
            return (WriteAction<TProperty>)cached;
        }

        // PERFORMANCE: Cache miss - build and store interceptor chain
        var writeInterceptors = GetServices<IWriteInterceptor>();
        var action = WriteInterceptorFactory<TProperty>.Create(writeInterceptors);
        _writeInterceptorFunction.TryAdd(typeof(TProperty), action);
        return action;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private InvokeFunc GetMethodInvocationFunction()
    {
        // PERFORMANCE: Local variable avoids multiple field reads
        var methodInvocationFunction = _methodInvocationFunction;
        if (methodInvocationFunction is not null)
        {
            return (InvokeFunc)methodInvocationFunction;
        }

        // PERFORMANCE: Double-check locking pattern
        // Lock only on cache miss (rare)
        lock (this)
        {
            methodInvocationFunction = _methodInvocationFunction;
            if (methodInvocationFunction is not null)
            {
                return (InvokeFunc)methodInvocationFunction;
            }

            var methodInterceptors = GetServices<IMethodInterceptor>();
            var func = MethodInvocationFactory.Create(methodInterceptors);
            _methodInvocationFunction = func;
            return func;
        }
    }

    /// <summary>
    /// PERFORMANCE: This method is called only on cache misses (under lock).
    /// After warm-up, this is rarely executed.
    /// </summary>
    private IEnumerable<TInterface> GetServicesWithoutCache<TInterface>()
    {
        // PERFORMANCE: Direct HashSet enumeration (no .Key property access like ConcurrentDictionary)
        // Distinct() removes duplicates from fallback chains
        return _services
            .OfType<TInterface>()
            .Concat(_fallbackContexts.SelectMany(c => c.GetServices<TInterface>()))
            .Distinct();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void OnContextChanged()
    {
        _serviceCache?.Clear();
        _readInterceptorFunction?.Clear();
        _writeInterceptorFunction?.Clear();
        _methodInvocationFunction = null;

        _noServicesSingleFallbackContext = _services.Count == 0 && _fallbackContexts.Count == 1
            ? _fallbackContexts.Single() : null;

        // ConcurrentDictionary enumeration is thread-safe (snapshot semantics)
        foreach (var parent in _usedByContexts)
        {
            parent.Key.OnContextChanged();
        }
    }
}
