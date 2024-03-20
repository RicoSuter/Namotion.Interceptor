namespace Namotion.Proxy.Handlers;

public interface IProxyPropertyHandler : IProxyHandler
{
    public void AttachProxy(ProxyWriteHandlerContext context, IProxy proxy);

    public void DetachProxy(ProxyWriteHandlerContext context, IProxy proxy);
}
