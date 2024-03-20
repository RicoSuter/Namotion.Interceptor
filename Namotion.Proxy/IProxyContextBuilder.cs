using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy
{
    public interface IProxyContextBuilder
    {
        ProxyContextBuilder AddHandler<T>(T handler)
            where T : IProxyHandler;

        ProxyContextBuilder TryAddSingleHandler<T>(T handler)
            where T : IProxyHandler;

        ProxyContext Build();
    }
}