using System.Collections;
using System.Collections.Concurrent;

namespace Namotion.Interceptor;

public class InterceptorSubjectContext : IInterceptorSubjectContext
{
    private DateTimeOffset _lastChange = DateTimeOffset.MinValue;
    
    private DateTimeOffset _cacheResetTime = DateTimeOffset.MinValue;
    private readonly ConcurrentDictionary<Type, IEnumerable> _serviceCache = new();
    
    private readonly List<IInterceptorSubjectContext> _contexts = [];
    private readonly List<object> _services = [];
    
    public static InterceptorSubjectContext Create()
    {
        return new InterceptorSubjectContext();
    }
    
    public bool HasChangedSince(DateTimeOffset time)
    {
        lock (_services)
        {
            if (_lastChange > time)
            {
                return true;
            }

            return _contexts.Any(c => c.HasChangedSince(time));
        }
    }

    public IEnumerable<TInterface> GetServices<TInterface>()
    {
        if (HasChangedSince(_cacheResetTime))
        {
            _serviceCache.Clear();
            _cacheResetTime = DateTimeOffset.UtcNow;
        }
        
        var services = _serviceCache.GetOrAdd(
            typeof(TInterface), 
            _ =>
            {
                lock (_services)
                {
                    return _services
                        .OfType<TInterface>()
                        .Concat(_contexts.SelectMany(c => c.GetServices<TInterface>()))
                        .Distinct()
                        .ToArray();
                }
            });

        return services.OfType<TInterface>();
    }
    
    public void AddFallbackContext(IInterceptorSubjectContext context)
    {
        lock (_services)
        {
            _contexts.Add(context);
            _lastChange = DateTimeOffset.UtcNow.AddSeconds(1);
        }
    }

    public void RemoveFallbackContext(IInterceptorSubjectContext context)
    {
        lock (_services)
        {
            _contexts.Remove(context);
            _lastChange = DateTimeOffset.UtcNow.AddSeconds(1);
        }
    }

    public bool TryAddService<TService>(Func<TService> factory, Func<TService, bool> exists)
    {
        if (GetServices<TService>().Any(exists))
        {
            return false;
        }

        AddService(factory());
        return true;
    }

    public void AddService<TService>(TService service)
    {
        lock (_services)
        {
            _services.Add(service!);
            _lastChange = DateTimeOffset.UtcNow.AddSeconds(1);
        }
    }

    public TInterface? TryGetService<TInterface>()
    {
        return GetServices<TInterface>().Single();
    }
}
