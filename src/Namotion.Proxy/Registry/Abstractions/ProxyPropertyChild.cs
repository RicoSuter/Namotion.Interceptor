using Namotion.Interceptor;

namespace Namotion.Proxy.Registry.Abstractions;

public readonly record struct ProxyPropertyChild
{
    public IInterceptorSubject Proxy { get; init; }

    public object? Index { get; init; }
}