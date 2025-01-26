namespace Namotion.Interceptor;

public interface IInterceptorCollection
{
    void AddInterceptorCollection(IInterceptorCollection interceptorCollection);

    void RemoveInterceptorCollection(IInterceptorCollection interceptorCollection);
    
    bool TryAddService<TService>(Func<TService> factory, Func<TService, bool> exists);

    void AddService<TService>(TService service);

    TInterface? TryGetService<TInterface>();

    IEnumerable<TInterface> GetServices<TInterface>();
}