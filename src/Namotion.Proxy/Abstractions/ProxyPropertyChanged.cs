using Namotion.Interceptor;

namespace Namotion.Proxy.Abstractions;

public record struct ProxyPropertyChanged(
    PropertyReference Property,
    object? OldValue,
    object? NewValue,
    IProxyContext Context)
{
}
