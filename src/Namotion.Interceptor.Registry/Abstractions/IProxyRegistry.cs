namespace Namotion.Interceptor.Registry.Abstractions;

public interface IProxyRegistry
{
    IReadOnlyDictionary<IInterceptorSubject, RegisteredProxy> KnownProxies { get; }
}
