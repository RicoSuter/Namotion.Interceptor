namespace Namotion.Proxy.Abstractions;

public interface IProxyChangedHandler : IProxyHandler
{
    void RaisePropertyChanged(ProxyChanged context);
}

public record struct ProxyChanged(
    IProxyContext Context,
    IProxy Proxy,
    string PropertyName,
    object? OldValue,
    object? NewValue)
{
}
