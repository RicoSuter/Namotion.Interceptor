namespace Namotion.Interceptor;

public interface IInterceptorSubjectContext
{
    void AddFallbackContext(IInterceptorSubjectContext context);

    void RemoveFallbackContext(IInterceptorSubjectContext context);
    
    bool TryAddService<TService>(Func<TService> factory, Func<TService, bool> exists);

    void AddService<TService>(TService service);

    TInterface? TryGetService<TInterface>();

    IEnumerable<TInterface> GetServices<TInterface>();
}