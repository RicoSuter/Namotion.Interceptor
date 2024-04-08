namespace Namotion.Proxy.Abstractions;

public record struct ReadProxyPropertyContext(
    ProxyPropertyReference Property,
    IProxyContext Context)
{
}
