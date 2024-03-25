namespace Namotion.Proxy.Registry.Abstractions;

public interface IProxyPropertyInitializer
{
    void InitializeProperty(ProxyProperty property, object? parentCollectionKey, IProxyContext context);
}