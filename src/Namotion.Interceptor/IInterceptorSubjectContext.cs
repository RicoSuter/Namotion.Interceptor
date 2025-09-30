using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor;

public interface IInterceptorSubjectContext
{
    void AddService<TService>(TService service);

    bool TryAddService<TService>(Func<TService> factory, Func<TService, bool> exists);

    TInterface? TryGetService<TInterface>();

    IEnumerable<TInterface> GetServices<TInterface>();

    TProperty ExecuteInterceptedRead<TProperty>(ref PropertyReadContext context, Func<IInterceptorSubject, TProperty> readValue);

    void ExecuteInterceptedWrite<TProperty>(ref PropertyWriteContext<TProperty> context, Action<IInterceptorSubject, TProperty> writeValue);

    object? InvokeMethod(IInterceptorSubject subject, string methodName, object?[] parameters, Func<object?[], object?> invokeMethod);
    
    bool AddFallbackContext(IInterceptorSubjectContext context);

    bool RemoveFallbackContext(IInterceptorSubjectContext context);
}