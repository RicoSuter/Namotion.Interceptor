using Namotion.Interceptor;

namespace Namotion.Interception.Lifecycle.Abstractions;

public record struct ProxyLifecycleContext(
    PropertyReference Property,
    object? Index,
    IInterceptorSubject Proxy,
    int ReferenceCount)
{
}
