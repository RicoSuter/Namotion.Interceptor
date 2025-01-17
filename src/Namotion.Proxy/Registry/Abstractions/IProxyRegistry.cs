using Namotion.Interceptor;

namespace Namotion.Proxy.Registry.Abstractions;

public interface IProxyRegistry
{
    IReadOnlyDictionary<IInterceptorSubject, RegisteredProxy> KnownProxies { get; }
}
