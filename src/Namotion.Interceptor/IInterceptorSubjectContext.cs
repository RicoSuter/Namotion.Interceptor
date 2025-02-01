namespace Namotion.Interceptor;

public interface IInterceptorSubjectContext
{
    internal bool HasChangedSince(DateTimeOffset time);
    
    void AddService<TService>(TService service);

    bool TryAddService<TService>(Func<TService> factory, Func<TService, bool> exists);

    TInterface? TryGetService<TInterface>();

    IEnumerable<TInterface> GetServices<TInterface>();

    void AddFallbackContext(IInterceptorSubjectContext context);

    void RemoveFallbackContext(IInterceptorSubjectContext context);
}