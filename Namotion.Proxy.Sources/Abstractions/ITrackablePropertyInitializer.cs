using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy.Sources.Abstractions;

public interface ITrackablePropertyInitializer
{
    void InitializeProperty(ProxyProperty property, object? parentCollectionKey, IProxyContext context);
}