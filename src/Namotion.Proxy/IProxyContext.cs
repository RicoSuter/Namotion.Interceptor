using Namotion.Interceptor;
using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy;

public interface IProxyContext : IInterceptorCollection
{
    IEnumerable<THandler> GetHandlers<THandler>()
        where THandler : IProxyHandler;
}
