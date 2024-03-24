namespace Namotion.Proxy.Abstractions;

public record struct ProxyPropertyChanged(
    IProxyContext Context,

    // TODO: Replace with ProxyPropertyReference?
    IProxy Proxy,
    string PropertyName,
    
    object? OldValue,
    object? NewValue)
{
}
