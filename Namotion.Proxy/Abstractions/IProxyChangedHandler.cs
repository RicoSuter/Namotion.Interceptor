namespace Namotion.Proxy.Abstractions;

public interface IProxyChangedHandler : IProxyHandler
{
    void RaisePropertyChanged(ProxyChangedHandlerContext context);
}

public record struct ProxyChangedHandlerContext(
    IProxyContext Context,
    IProxy Proxy,
    string PropertyName,
    object? previousValue,
    object? newValue)
{
}
