namespace Namotion.Proxy.Abstractions;

public interface IProxyChangedHandler : IProxyHandler
{
    void RaisePropertyChanged(ProxyChangedContext context);
}

public record struct ProxyChangedContext(
    IProxyContext Context,
    IProxy Proxy,
    string PropertyName,
    object? OldValue,
    object? NewValue)
{
}
