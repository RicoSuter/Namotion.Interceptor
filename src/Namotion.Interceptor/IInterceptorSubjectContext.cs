namespace Namotion.Interceptor;

public interface IInterceptorSubjectContext
{
    void AddService<TService>(TService service);

    bool TryAddService<TService>(Func<TService> factory, Func<TService, bool> exists);

    TInterface? TryGetService<TInterface>();

    IEnumerable<TInterface> GetServices<TInterface>();

    TProperty ExecuteInterceptedRead<TProperty>(ReadPropertyInterception interception, Func<IInterceptorSubject, TProperty> readValue);

    void ExecuteInterceptedWrite<TProperty>(WritePropertyInterception interception, Action<IInterceptorSubject, TProperty> writeValue);

    bool AddFallbackContext(IInterceptorSubjectContext context);

    bool RemoveFallbackContext(IInterceptorSubjectContext context);
}