using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy.Registry.Abstractions;

public interface IProxyRegistry : IProxyHandler
{
    IReadOnlyDictionary<IProxy, ProxyMetadata> KnownProxies { get; }
}
