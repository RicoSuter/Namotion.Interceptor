using Namotion.Interceptor;
using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy;

public class ProxyContextBuilder : IProxyContextBuilder
{
    private readonly List<(Func<IProxyContext, IProxyHandler>, Type)> _handlers = [];

    public ProxyContextBuilder AddHandler<T>(Func<IProxyContext, T> handler)
        where T : IProxyHandler
    {
        _handlers.Add((context => handler(context), typeof(T)));
        return this;
    }

    public ProxyContextBuilder TryAddSingleHandler<T>(Func<IProxyContext, T> handler)
        where T : IProxyHandler
    {
        if (_handlers.Any(h => h.Item2 == typeof(T)) == false)
        {
            _handlers.Add((context => handler(context), typeof(T)));
        }
        return this;
    }

    public ProxyContext Build()
    {
        return new ProxyContext(_handlers.Select(p => p.Item1));
    }

    public Lazy<THandler[]> GetLazyHandlers<THandler>(IProxyContext context)
        where THandler : IProxyHandler
    {
        return new Lazy<THandler[]>(() => context.GetHandlers<THandler>().ToArray());
    }
}
