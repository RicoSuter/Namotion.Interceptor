using Namotion.Proxy.Registry.Abstractions;

namespace Namotion.Proxy.Registry;

public static class ProxyRegistryExtensions
{
    public static IEnumerable<ProxyProperty> GetProperties(this IProxyRegistry registry)
    {
        foreach (var pair in registry.KnownProxies)
        {
            foreach (var property in pair.Value.Properties.Values)
            {
                yield return property;
            }
        }
    }
}
