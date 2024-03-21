using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy.ChangeTracking;

internal class InitiallyLoadDerivedPropertiesHandler : IProxyLifecycleHandler
{
    public void AttachProxy(ProxyPropertyRegistryHandlerContext context, IProxy proxy)
    {
        foreach (var property in proxy.Properties.Where(p => p.Value.IsDerived))
        {
            property.Value.ReadValue(proxy);
        }
    }

    public void DetachProxy(ProxyPropertyRegistryHandlerContext context, IProxy proxy)
    {
    }
}
