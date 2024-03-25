using Namotion.Proxy.Registry.Abstractions;

namespace Namotion.Proxy.Registry;

public static class ProxyRegistryExtensions
{
    public static IEnumerable<ProxyPropertyMetadata> GetProperties(this IProxyRegistry registry)
    {
        foreach (var pair in registry.KnownProxies)
        {
            foreach (var property in pair.Value.Properties.Values)
            {
                yield return property;
            }
        }
    }

    public static ProxyPropertyMetadata? TryGetProperty(this IReadOnlyDictionary<IProxy, ProxyMetadata> properties, ProxyPropertyReference property)
    {
        if (properties.TryGetValue(property.Proxy, out var metadata))
        {
            if (metadata.Properties.TryGetValue(property.Name, out var result))
            {
                return result;
            }
        }

        return null;
    }
}
