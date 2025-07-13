namespace Namotion.Interceptor;

public interface IInterceptorSubjectContext
{
    void AddService<TService>(TService service);

    bool TryAddService<TService>(Func<TService> factory, Func<TService, bool> exists);

    TInterface? TryGetService<TInterface>();

    IEnumerable<TInterface> GetServices<TInterface>();

    object? ExecuteInterceptedRead(ReadPropertyInterception interception, Func<object?> readValue);

    void ExecuteInterceptedWrite(WritePropertyInterception interception, Action<object?> writeValue);

    void AddFallbackContext(IInterceptorSubjectContext context);

    void RemoveFallbackContext(IInterceptorSubjectContext context);
}