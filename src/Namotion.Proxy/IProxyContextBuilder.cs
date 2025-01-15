using Namotion.Interceptor;
using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy
{
    public interface IProxyContextBuilder
    {
        Lazy<THandler[]> GetLazyHandlers<THandler>(IProxyContext context)
            where THandler : IProxyHandler;

        ProxyContextBuilder AddHandler<T>(Func<IProxyContext, T> handler)
            where T : IProxyHandler;

        ProxyContextBuilder TryAddSingleHandler<T>(Func<IProxyContext, T> handler)
            where T : IProxyHandler;

        ProxyContext Build();
    }
}