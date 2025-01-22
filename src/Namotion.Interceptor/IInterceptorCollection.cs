namespace Namotion.Interceptor;

public interface IInterceptorCollection
{
    void AddInterceptorCollection(IInterceptorCollection interceptorCollection);

    void RemoveInterceptorCollection(IInterceptorCollection interceptorCollection);
    
    bool TryAddService<TInterface, TService>(Func<TService> factory);

    void AddService<TService>(TService service);

    TInterface? TryGetService<TInterface>();

    IEnumerable<TInterface> GetServices<TInterface>();
}