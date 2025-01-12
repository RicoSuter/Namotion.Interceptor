using Namotion.Interceptor;
using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy;

public class ProxyContext : InterceptorManager, IProxyContext
{
    private readonly IEnumerable<IProxyHandler> _handlers;

    public static ProxyContextBuilder CreateBuilder()
    {
        return new ProxyContextBuilder();
    }

    public ProxyContext(IEnumerable<IProxyHandler> handlers)
        : base(
            handlers.OfType<IReadInterceptor>().Reverse().ToArray(), 
            handlers.OfType<IWriteInterceptor>().Reverse().ToArray())
    {
        _handlers = handlers.ToArray();
    }

    public IEnumerable<THandler> GetHandlers<THandler>()
        where THandler : IProxyHandler
    {
        return _handlers.OfType<THandler>();
    }
}
