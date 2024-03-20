namespace Namotion.Proxy.Handlers;

public interface IProxyWriteHandler : IProxyHandler
{
    void SetProperty(ProxyWriteHandlerContext context, Action<ProxyWriteHandlerContext> next);
}

public record struct ProxyWriteHandlerContext(
    IProxyContext Context,
    object Proxy,
    string PropertyName,
    object? NewValue,
    Func<object?> ReadValue)
{
}
