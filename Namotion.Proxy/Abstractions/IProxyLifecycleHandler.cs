namespace Namotion.Proxy.Abstractions;

public interface IProxyLifecycleHandler : IProxyHandler
{
    public void OnProxyAttached(ProxyLifecycleContext context);

    public void OnProxyDetached(ProxyLifecycleContext context);
}

public record struct ProxyLifecycleContext(
    ProxyPropertyReference Property,
    object? Index,
    IProxy Proxy,
    int ReferenceCount,
    IProxyContext Context)
{
}
