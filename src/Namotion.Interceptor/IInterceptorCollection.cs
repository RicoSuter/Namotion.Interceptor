namespace Namotion.Interceptor;

public interface IInterceptorCollection
{
    void AddFallbackCollection(IInterceptorCollection interceptorCollection);

    void RemoveFallbackCollection(IInterceptorCollection interceptorCollection);
    
    bool TryAddService<TService>(Func<TService> factory, Func<TService, bool> exists);

    void AddService<TService>(TService service);

    TInterface? TryGetService<TInterface>();

    IEnumerable<TInterface> GetServices<TInterface>();
}