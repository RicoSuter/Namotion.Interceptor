namespace Namotion.Proxy.Abstractions;

public record struct WriteProxyPropertyContext(
    ProxyPropertyReference Property,
    object? CurrentValue,
    object? NewValue,
    IProxyContext Context)
{
}
