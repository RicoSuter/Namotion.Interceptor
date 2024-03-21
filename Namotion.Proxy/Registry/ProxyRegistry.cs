using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy.Registry;

internal class ProxyRegistry : IProxyRegistry, IProxyLifecycleHandler
{
    private HashSet<IProxy> _knownProxies = new HashSet<IProxy>();

    public IEnumerable<IProxy> KnownProxies
    {
        get
        {
            lock (_knownProxies)
                return _knownProxies.ToArray();
        }
    }

    public void AttachProxy(ProxyPropertyRegistryHandlerContext context, IProxy proxy)
    {
        lock (_knownProxies)
            _knownProxies.Add(proxy);
    }

    public void DetachProxy(ProxyPropertyRegistryHandlerContext context, IProxy proxy)
    {
        lock (_knownProxies)
            _knownProxies.Remove(proxy);
    }
}
