namespace Namotion.Interceptor;

public class InterceptorCollection : IInterceptorCollection
{
    private readonly List<IInterceptorCollection> _interceptorCollections = [];
    private readonly List<object> _services = [];

    public static InterceptorCollection Create()
    {
        return new InterceptorCollection();
    }
    
    public void AddFallbackCollection(IInterceptorCollection interceptorCollection)
    {
        _interceptorCollections.Add(interceptorCollection);
    }

    public void RemoveFallbackCollection(IInterceptorCollection interceptorCollection)
    {
        _interceptorCollections.Remove(interceptorCollection);
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
        _services.Add(service!);
    }

    public TInterface? TryGetService<TInterface>()
    {
        return GetServices<TInterface>().Single();
    }
    
    public IEnumerable<TInterface> GetServices<TInterface>()
    {
        return _services
            .OfType<TInterface>()
            .Concat(_interceptorCollections.SelectMany(c => c.GetServices<TInterface>()))
            .Distinct();
    }
}
