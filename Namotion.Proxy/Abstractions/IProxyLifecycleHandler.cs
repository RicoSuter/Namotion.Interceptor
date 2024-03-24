namespace Namotion.Proxy.Abstractions;

public interface IProxyLifecycleHandler : IProxyHandler
{
    public void OnProxyAttached(ProxyLifecycleContext context);

    public void OnProxyDetached(ProxyLifecycleContext context);
}

public record struct ProxyLifecycleContext(
    IProxyContext Context,
    IProxy? ParentProxy,
    string PropertyName,
    object? Index,
    IProxy Proxy,
    int ReferenceCount)
{
}
