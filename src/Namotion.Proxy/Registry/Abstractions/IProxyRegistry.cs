using Namotion.Interceptor;
using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy.Registry.Abstractions;

public interface IProxyRegistry
{
    IReadOnlyDictionary<IInterceptorSubject, RegisteredProxy> KnownProxies { get; }
}
