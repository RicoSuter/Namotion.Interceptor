using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy.Handlers;

internal class ParentsHandler : IProxyPropertyRegistryHandler
{
    public void AttachProxy(ProxyPropertyRegistryHandlerContext context, IProxy proxy)
    {
        if (context.ParentProxy is not null)
        {
            proxy.AddParent(context.ParentProxy);
        }
    }

    public void DetachProxy(ProxyPropertyRegistryHandlerContext context, IProxy proxy)
    {
        if (context.ParentProxy is not null)
        {
            proxy.RemoveParent(context.ParentProxy);
        }
    }
}
