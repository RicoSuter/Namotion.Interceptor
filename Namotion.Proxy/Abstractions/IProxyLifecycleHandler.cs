using Namotion.Proxy.ChangeTracking;

namespace Namotion.Proxy.Abstractions;

public interface IProxyLifecycleHandler : IProxyHandler
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
