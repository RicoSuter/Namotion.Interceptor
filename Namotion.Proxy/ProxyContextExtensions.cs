using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy;

public static class ProxyContextExtensions
{
    public static THandler GetHandler<THandler>(this IProxyContext context)
        where THandler : IProxyHandler
    {
        return context.GetHandlers<THandler>().Single();
    }
}
