using Namotion.Interceptor;
using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy;

public interface IProxyContext : IInterceptor
{
    IEnumerable<THandler> GetHandlers<THandler>()
        where THandler : IProxyHandler;
}
