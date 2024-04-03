namespace Namotion.Proxy.Registry.Abstractions;

public interface IProxyPropertyInitializer
{
    void InitializeProperty(ProxyPropertyMetadata property, object? index, IProxyContext context);
}