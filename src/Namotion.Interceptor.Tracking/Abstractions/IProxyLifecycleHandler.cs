namespace Namotion.Interception.Lifecycle.Abstractions;

public interface IProxyLifecycleHandler
{
    public void OnProxyAttached(ProxyLifecycleContext context);

    public void OnProxyDetached(ProxyLifecycleContext context);
}
