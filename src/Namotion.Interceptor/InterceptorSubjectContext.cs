using System.Collections;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Namotion.Interceptor;

public class InterceptorSubjectContext : IInterceptorSubjectContext
{
    private Lazy<Func<ReadPropertyInterception, Func<object?>, object?>> _readInterceptorFunction;

    private Lazy<Func<WritePropertyInterception, Action<object?>, object?>> _writeInterceptorFunction;

    private readonly ConcurrentDictionary<Type, IEnumerable> _serviceCache = new();

    private readonly HashSet<object> _services = [];

    private readonly HashSet<InterceptorSubjectContext> _usedByContexts = [];
    private readonly HashSet<InterceptorSubjectContext> _fallbackContexts = [];
    private InterceptorSubjectContext? _noServicesSingleFallbackContext;

#pragma warning disable CS8618
    public InterceptorSubjectContext()
    {
        ResetInterceptorFunctions();
    }

    public static InterceptorSubjectContext Create()
    {
        return new InterceptorSubjectContext();
    }

    private void ResetInterceptorFunctions()
    {
        _readInterceptorFunction = new Lazy<Func<ReadPropertyInterception, Func<object?>, object?>>(() =>
        {
            var returnReadValue = new Func<ReadPropertyInterception, Func<object?>, object?>((_, innerReadValue) => innerReadValue());

            var readInterceptors = GetServices<IReadInterceptor>();
            foreach (var handler in readInterceptors)
            {
                var previousReadValue = returnReadValue;
                returnReadValue = (context, innerReadValue) =>
                    handler.ReadProperty(context, ctx => previousReadValue(ctx, innerReadValue));
            }

            return returnReadValue;
        });

        _writeInterceptorFunction = new Lazy<Func<WritePropertyInterception, Action<object?>, object?>>(() =>
        {
            var returnWriteValue = new Func<WritePropertyInterception, Action<object?>, object?>(
                (value, innerWriteValue) =>
                {
                    innerWriteValue(value.NewValue);
                    return value.NewValue;
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
    }

    public object? ExecuteInterceptedRead(ReadPropertyInterception interception, Func<object?> readValue)
    {
        return _readInterceptorFunction.Value(interception, readValue);
    }

    public void ExecuteInterceptedWrite(
        WritePropertyInterception interception,
        Action<object?> writeValue)
    {
        _writeInterceptorFunction.Value(interception, writeValue);
    }

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
        return GetServices<TInterface>().SingleOrDefault();
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