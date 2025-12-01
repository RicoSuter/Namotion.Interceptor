using System.Collections.Immutable;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor;

public interface IInterceptorSubjectContext
{
    void AddService<TService>(TService service);

    bool TryAddService<TService>(Func<TService> factory, Func<TService, bool> exists);

    TInterface? TryGetService<TInterface>();

    ImmutableArray<TInterface> GetServices<TInterface>();

    TProperty ExecuteInterceptedRead<TProperty>(ref PropertyReadContext context, Func<IInterceptorSubject, TProperty> readValue);

    void ExecuteInterceptedWrite<TProperty>(ref PropertyWriteContext<TProperty> context, Action<IInterceptorSubject, TProperty> writeValue);

    object? ExecuteInterceptedInvoke(ref MethodInvocationContext context, Func<IInterceptorSubject, object?[], object?> invokeMethod);

    bool AddFallbackContext(IInterceptorSubjectContext context);

    bool RemoveFallbackContext(IInterceptorSubjectContext context);
}