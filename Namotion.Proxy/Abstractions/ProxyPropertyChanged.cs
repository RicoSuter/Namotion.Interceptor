namespace Namotion.Proxy.Abstractions;

public record struct ProxyPropertyChanged(
    IProxyContext Context,
    IProxy Proxy,
    string PropertyName,
    object? OldValue,
    object? NewValue)
{
}
