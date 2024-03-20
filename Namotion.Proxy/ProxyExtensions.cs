namespace Namotion.Proxy;

public static class ProxyExtensions
{
    public static void SetContext(this IProxy proxy, IProxyContext context)
    {
        proxy.Context = context;
    }
}
