namespace Namotion.Proxy.Handlers;

public class AutoProxyContextHandler : IProxyPropertyHandler
{
    public void AttachProxy(ProxyWriteHandlerContext context, IProxy proxy)
    {
        proxy.Context = context.Context;
    }

    public void DetachProxy(ProxyWriteHandlerContext context, IProxy proxy)
    {
        proxy.Context = null;
    }
}
