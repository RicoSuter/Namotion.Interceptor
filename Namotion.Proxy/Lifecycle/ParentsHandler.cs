using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy.Lifecycle;

internal class ParentsHandler : IProxyLifecycleHandler
{
    public void OnProxyAttached(ProxyLifecycleContext context)
    {
        if (context.ParentProxy is not null)
        {
            context.Proxy.AddParent(context.ParentProxy);
        }
    }

    public void OnProxyDetached(ProxyLifecycleContext context)
    {
        if (context.ParentProxy is not null)
        {
            context.Proxy.RemoveParent(context.ParentProxy);
        }
    }
}
