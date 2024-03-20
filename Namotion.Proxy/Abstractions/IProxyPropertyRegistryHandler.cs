namespace Namotion.Proxy.Abstractions;

public interface IProxyPropertyRegistryHandler : IProxyHandler
{
    public void AttachProxy(ProxyWriteHandlerContext context, IProxy proxy);

    public void DetachProxy(ProxyWriteHandlerContext context, IProxy proxy);
}
