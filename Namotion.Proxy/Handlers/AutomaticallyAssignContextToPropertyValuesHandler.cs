using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy.Handlers;

internal class AutomaticallyAssignContextToPropertyValuesHandler : IProxyPropertyRegistryHandler
{
    public void AttachProxy(ProxyPropertyRegistryHandlerContext context, IProxy proxy)
    {
        proxy.Context = context.Context;
    }

    public void DetachProxy(ProxyPropertyRegistryHandlerContext context, IProxy proxy)
    {
        proxy.Context = null;
    }
}
