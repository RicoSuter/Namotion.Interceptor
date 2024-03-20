using Namotion.Proxy.Handlers;

namespace Namotion.Proxy.Abstractions;

public interface IProxyPropertyRegistryHandler : IProxyHandler
{
    public void AttachProxy(ProxyPropertyRegistryHandlerContext context, IProxy proxy);

    public void DetachProxy(ProxyPropertyRegistryHandlerContext context, IProxy proxy);
}

public record struct ProxyPropertyRegistryHandlerContext(
    IProxyContext Context,
    IProxy? ParentProxy,
    string PropertyName,
    object? Index,
    IProxy? Proxy,
    int ReferenceCount)
{
}
