using Namotion.Interceptor;
using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy.Registry.Abstractions;

public interface IProxyRegistry : IProxyHandler
{
    IReadOnlyDictionary<IInterceptorSubject, RegisteredProxy> KnownProxies { get; }
}
