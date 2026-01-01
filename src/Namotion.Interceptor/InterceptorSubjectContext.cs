using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Cache;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Ordering;

namespace Namotion.Interceptor;

public class InterceptorSubjectContext : IInterceptorSubjectContext
{
    [ThreadStatic]
    private static HashSet<InterceptorSubjectContext>? _visitedCache;

    [ThreadStatic]
    private static int _visitedDepth;

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
                lock (this)
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
            lock (this)
            {
                _serviceCache = new ConcurrentDictionary<Type, object>();
                _readInterceptorFunction = new ConcurrentDictionary<Type, Delegate>();
                _writeInterceptorFunction = new ConcurrentDictionary<Type, Delegate>();
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
                contextImpl._usedByContexts.Add(this);
                OnContextChanged();
                return true;
            }

            return false;
        }
    }

    protected bool HasFallbackContext(IInterceptorSubjectContext context)
    {
        lock (this)
            return _fallbackContexts.Contains(context);
    }

    public virtual bool RemoveFallbackContext(IInterceptorSubjectContext context)
    {
        var contextImpl = (InterceptorSubjectContext)context;
        lock (this)
        {
            if (_fallbackContexts.Remove(contextImpl))
            {
                contextImpl._usedByContexts.Remove(this);
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

            AddService(factory());
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
        var services = GetServices<TInterface>();
        return services.Length switch
        {
            1 => services[0],
            0 => default,
            _ => throw new InvalidOperationException($"There must be exactly one service of type {typeof(TInterface).FullName}.")
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TProperty ExecuteInterceptedRead<TProperty>(ref PropertyReadContext context, Func<IInterceptorSubject, TProperty> readValue)
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
    public void ExecuteInterceptedWrite<TProperty>(ref PropertyWriteContext<TProperty> context, Action<IInterceptorSubject, TProperty> writeValue)
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
    public object? ExecuteInterceptedInvoke(ref MethodInvocationContext context, Func<IInterceptorSubject, object?[], object?> invokeMethod)
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

        lock (this)
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
        var visited = _visitedCache ??= [];
        _visitedDepth++;
        try
        {
            return GetServicesWithoutCache(typeof(TInterface), visited)
                .OfType<TInterface>()
                .ToArray();
        }
        finally
        {
            if (--_visitedDepth == 0)
            {
                visited.Clear();
            }
        }
    }

    private IEnumerable<object> GetServicesWithoutCache(Type type, HashSet<InterceptorSubjectContext> visited)
    {
        if (!visited.Add(this))
        {
            return [];
        }

        var services = _services
            .Where(type.IsInstanceOfType)
            .Concat(_fallbackContexts.SelectMany(c => c.GetServicesWithoutCache(type, visited)))
            .Distinct()
            .ToArray();

        return ServiceOrderResolver.OrderByDependencies(services);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void OnContextChanged()
    {
        var visited = _visitedCache ??= [];
        _visitedDepth++;
        try
        {
            OnContextChanged(visited);
        }
        finally
        {
            if (--_visitedDepth == 0)
            {
                visited.Clear();
            }
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

        _noServicesSingleFallbackContext = _services.Count == 0 && _fallbackContexts.Count == 1
            ? _fallbackContexts.Single() : null;

        foreach (var parent in _usedByContexts)
        {
            parent.OnContextChanged(visited);
        }
    }
}