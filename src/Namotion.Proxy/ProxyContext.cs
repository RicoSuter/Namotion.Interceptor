using Namotion.Interceptor;
using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy;

public class ProxyContext : InterceptorCollection, IProxyContext
{
    private IEnumerable<IProxyHandler> _handlers = null!;

    public static ProxyContextBuilder CreateBuilder()
    {
        return new ProxyContextBuilder();
    }

    public ProxyContext(IEnumerable<IProxyHandler> handlers)
    {
        SetHandlers(handlers);
    }
    
    public ProxyContext(IEnumerable<Func<IProxyContext, IProxyHandler>> handlers)
    {
        SetHandlers(handlers.Select(h => h(this)).ToArray());
    }
    
    private void SetHandlers(IEnumerable<IProxyHandler> handlers)
    {
        _handlers = handlers.ToArray();

        SetHandlers(
            _handlers.OfType<IReadInterceptor>().Reverse().ToArray(), 
            _handlers.OfType<IWriteInterceptor>().Reverse().ToArray());
    }

    public IEnumerable<THandler> GetHandlers<THandler>()
        where THandler : IProxyHandler
    {
        return _handlers.OfType<THandler>();
    }
}
