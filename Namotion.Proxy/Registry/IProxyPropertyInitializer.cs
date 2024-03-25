using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy.Sources.Abstractions;

public interface IProxyPropertyInitializer
{
    void InitializeProperty(ProxyProperty property, object? parentCollectionKey, IProxyContext context);
}