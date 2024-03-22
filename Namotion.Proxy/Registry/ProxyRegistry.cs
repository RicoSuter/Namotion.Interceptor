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

    public void OnProxyAttached(ProxyLifecycleContext context)
    {
        lock (_knownProxies)
            _knownProxies.Add(context.Proxy);
    }

    public void OnProxyDetached(ProxyLifecycleContext context)
    {
        lock (_knownProxies)
            _knownProxies.Remove(context.Proxy);
    }
}
