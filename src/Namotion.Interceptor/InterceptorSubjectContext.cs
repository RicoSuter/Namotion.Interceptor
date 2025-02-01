namespace Namotion.Interceptor;

public class InterceptorSubjectContext : IInterceptorSubjectContext
{
    private readonly List<IInterceptorSubjectContext> _contexts = [];
    private readonly List<object> _services = []; // TODO: Do we need locking here?

    public static InterceptorSubjectContext Create()
    {
        return new InterceptorSubjectContext();
    }
    
    public void AddFallbackContext(IInterceptorSubjectContext context)
    {
        _contexts.Add(context);
    }

    public void RemoveFallbackContext(IInterceptorSubjectContext context)
    {
        _contexts.Remove(context);
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
            .Concat(_contexts.SelectMany(c => c.GetServices<TInterface>()))
            .Distinct();
    }
}
