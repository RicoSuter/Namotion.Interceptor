using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy.Lifecycle;

internal class ParentsHandler : IProxyLifecycleHandler
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
