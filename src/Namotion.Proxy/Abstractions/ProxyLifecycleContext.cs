using Namotion.Interceptor;

namespace Namotion.Proxy.Abstractions;

public record struct ProxyLifecycleContext(
    PropertyReference Property,
    object? Index,
    IInterceptorSubject Proxy,
    int ReferenceCount,
    IProxyContext Context)
{
}
