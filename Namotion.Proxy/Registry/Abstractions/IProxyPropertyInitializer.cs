namespace Namotion.Proxy.Registry.Abstractions;

public interface IProxyPropertyInitializer
{
    void InitializeProperty(ProxyPropertyMetadata property, object? parentCollectionKey, IProxyContext context);
}