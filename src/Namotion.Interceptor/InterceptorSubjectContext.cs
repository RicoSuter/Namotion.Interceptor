using System.Collections;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Namotion.Interceptor;

public class InterceptorSubjectContext : IInterceptorSubjectContext
{
    // TODO(perf): Do not initialize these dictionaries until they are needed

    private readonly ConcurrentDictionary<Type, Delegate> _readInterceptorFunction = new();
    private readonly ConcurrentDictionary<Type, Delegate> _writeInterceptorFunction = new();
    private readonly ConcurrentDictionary<Type, IEnumerable> _serviceCache = new();

    private readonly HashSet<object> _services = [];

    private readonly HashSet<InterceptorSubjectContext> _usedByContexts = [];
    private readonly HashSet<InterceptorSubjectContext> _fallbackContexts = [];
    private InterceptorSubjectContext? _noServicesSingleFallbackContext;

    public static InterceptorSubjectContext Create()
    {
        return new InterceptorSubjectContext();
    }

    private void ResetInterceptorFunctions()
    { 
        var noServicesSingleFallbackContext = _noServicesSingleFallbackContext;
        if (noServicesSingleFallbackContext is not null)
        {
            return;
        }
        
        _readInterceptorFunction.Clear();
        _writeInterceptorFunction.Clear();
    }

    public TProperty ExecuteInterceptedRead<TProperty>(ReadPropertyInterception interception, Func<IInterceptorSubject, TProperty> readValue)
    {
        var noServicesSingleFallbackContext = _noServicesSingleFallbackContext;
        if (noServicesSingleFallbackContext is not null)
        {
            return noServicesSingleFallbackContext.ExecuteInterceptedRead(interception, readValue);
        }

        var func = (Func<ReadPropertyInterception, Func<IInterceptorSubject, TProperty>, TProperty>)
            _readInterceptorFunction.GetOrAdd(typeof(TProperty), _ =>
            {
                var returnReadValue = new Func<ReadPropertyInterception, Func<IInterceptorSubject, TProperty>, TProperty>(
                    (i, innerReadValue) => innerReadValue(i.Property.Subject));

                var readInterceptors = GetServices<IReadInterceptor>();
                foreach (var handler in readInterceptors)
                {
                    var previousReadValue = returnReadValue;
                    returnReadValue = (context, innerReadValue) =>
                        handler.ReadProperty(context, ctx => previousReadValue(ctx, innerReadValue));
                }

                return returnReadValue;
            });

        return func(interception, readValue);
    }

    public void ExecuteInterceptedWrite<TProperty>(WritePropertyInterception interception, Action<IInterceptorSubject, TProperty> writeValue)
    {
        var noServicesSingleFallbackContext = _noServicesSingleFallbackContext;
        if (noServicesSingleFallbackContext is not null)
        {
            noServicesSingleFallbackContext.ExecuteInterceptedWrite(interception, writeValue);
            return;
        }
        
        var func = (Func<WritePropertyInterception, Action<IInterceptorSubject, TProperty>, TProperty>)
            _writeInterceptorFunction.GetOrAdd(typeof(TProperty), _ =>
            {
                var returnWriteValue = new Func<WritePropertyInterception, Action<IInterceptorSubject, TProperty>, TProperty>(
                    (i, innerWriteValue) =>
                    {
                        innerWriteValue(i.Property.Subject, (TProperty)i.NewValue!);
                        return (TProperty)i.NewValue!;
                    });

                var readInterceptors = GetServices<IWriteInterceptor>();
                foreach (var handler in readInterceptors)
                {
                    var previousWriteValue = returnWriteValue;
                    returnWriteValue = (context, innerWriteValue) =>
                        handler.WriteProperty(context, ctx => previousWriteValue(ctx, innerWriteValue));
                }

                return returnWriteValue;
            });

        func(interception, writeValue);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IEnumerable<TInterface> GetServices<TInterface>()
    {
        // When there is only a fallback context and no services then we do not
        // need to create an own cache and waste time creating and maintaining it.
        // We can just redirect the call to the fallback context.
        var noServicesSingleFallbackContext = _noServicesSingleFallbackContext;
        if (noServicesSingleFallbackContext is not null)
        {
            return noServicesSingleFallbackContext.GetServices<TInterface>();
        }

        var services = _serviceCache.GetOrAdd(
            typeof(TInterface), _ =>
            {
                lock (_services)
                    return GetServicesWithoutCache<TInterface>().ToArray();
            });

        return (TInterface[])services;
    }

    public virtual bool AddFallbackContext(IInterceptorSubjectContext context)
    {
        var contextImpl = (InterceptorSubjectContext)context;
        lock (_services)
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
        lock (_services)
            return _fallbackContexts.Contains(context);
    }

    public virtual bool RemoveFallbackContext(IInterceptorSubjectContext context)
    {
        var contextImpl = (InterceptorSubjectContext)context;
        lock (_services)
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
        lock (_services)
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
        lock (_services)
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

    private IEnumerable<TInterface> GetServicesWithoutCache<TInterface>()
    {
        return _services
            .OfType<TInterface>()
            .Concat(_fallbackContexts.SelectMany(c => c.GetServices<TInterface>()))
            .Distinct();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void OnContextChanged()
    {
        _serviceCache.Clear();
        _noServicesSingleFallbackContext = _services.Count == 0 && _fallbackContexts.Count == 1 
            ? _fallbackContexts.Single() : null;

        ResetInterceptorFunctions();
        foreach (var parent in _usedByContexts)
        {
            parent.OnContextChanged();
        }
    }
}