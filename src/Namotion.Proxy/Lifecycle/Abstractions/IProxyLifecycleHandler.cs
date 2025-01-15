using Namotion.Interceptor;

namespace Namotion.Proxy.Abstractions;

public interface IProxyLifecycleHandler
{
    public void OnProxyAttached(ProxyLifecycleContext context);

    public void OnProxyDetached(ProxyLifecycleContext context);
}
