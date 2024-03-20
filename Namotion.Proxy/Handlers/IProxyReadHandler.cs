namespace Namotion.Proxy.Handlers;

public interface IProxyReadHandler : IProxyHandler
{
    object? GetProperty(ProxyReadHandlerContext context, Func<ProxyReadHandlerContext, object?> next);
}

public record struct ProxyReadHandlerContext(
    IProxyContext Context,
    IProxy Proxy,
    string PropertyName)
{
}
