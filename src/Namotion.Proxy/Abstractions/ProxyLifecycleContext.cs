using Namotion.Interceptor;

namespace Namotion.Proxy.Abstractions;

public record struct ProxyLifecycleContext(
    ProxyPropertyReference Property,
    object? Index,
    IInterceptorSubject Proxy,
    int ReferenceCount,
    IProxyContext Context)
{
}
