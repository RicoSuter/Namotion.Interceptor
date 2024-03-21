using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy.Lifecycle;

internal class AutomaticContextAssignmentHandler : IProxyLifecycleHandler
{
    public void AttachProxy(ProxyPropertyRegistryHandlerContext context, IProxy proxy)
    {
        if (context.ReferenceCount == 1)
        {
            proxy.Context = context.Context;
        }
    }

    public void DetachProxy(ProxyPropertyRegistryHandlerContext context, IProxy proxy)
    {
        if (context.ReferenceCount == 0)
        {
            proxy.Context = null;
        }
    }
}
