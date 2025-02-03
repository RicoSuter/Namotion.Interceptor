using System.Collections;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Namotion.Interceptor;

public class InterceptorSubjectContext : IInterceptorSubjectContext
{
    private readonly ConcurrentDictionary<Type, IEnumerable> _serviceCache = new();

    private readonly HashSet<object> _services = [];

    private readonly HashSet<InterceptorSubjectContext> _usedByContexts = [];
    private readonly HashSet<InterceptorSubjectContext> _fallbackContexts = [];

    public static InterceptorSubjectContext Create()
    {
        return new InterceptorSubjectContext();
    }

    public IEnumerable<TInterface> GetServices<TInterface>()
    {
        var services = _serviceCache.GetOrAdd(
            typeof(TInterface), _ =>
            {
                lock (_services)
                    return GetServicesWithoutCache<TInterface>().ToArray();
            });

        return services.OfType<TInterface>();
    }

    public virtual void AddFallbackContext(IInterceptorSubjectContext context)
    {
        var contextImpl = (InterceptorSubjectContext)context;
        lock (_services)
        {
            contextImpl._usedByContexts.Add(this);
            _fallbackContexts.Add(contextImpl);
            OnContextChanged();
        }
    }

    public virtual void RemoveFallbackContext(IInterceptorSubjectContext context)
    {
        var contextImpl = (InterceptorSubjectContext)context;
        lock (_services)
        {
            _fallbackContexts.Remove(contextImpl);
            contextImpl._usedByContexts.Remove(this);
            OnContextChanged();
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

        foreach (var parent in _usedByContexts)
        {
            parent.OnContextChanged();
        }
    }
}