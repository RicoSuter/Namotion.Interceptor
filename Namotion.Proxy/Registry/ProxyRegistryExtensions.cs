using Namotion.Proxy.Registry.Abstractions;
using Namotion.Proxy.Registry.Attributes;

using System.Collections;
using System.Text.Json.Nodes;

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

    public static JsonObject SerializeProxyToJson(this IProxyRegistry registry, IProxy proxy)
    {
        var obj = new JsonObject();
        if (registry.KnownProxies.TryGetValue(proxy, out var metadata))
        {
            foreach (var property in metadata
                .Properties
                .Where(p => p.Value.GetValue is not null))
            {
                var name = metadata.GetJsonPropertyName(property.Key, property.Value);
                var value = property.Value.GetValue?.Invoke();
                if (value is IProxy childProxy)
                {
                    obj[name] = SerializeProxyToJson(registry, childProxy);
                }
                else if (value is ICollection collection && collection.OfType<IProxy>().Any())
                {
                    var children = new JsonArray();
                    foreach (var arrayProxyItem in collection.OfType<IProxy>())
                    {
                        children.Add(SerializeProxyToJson(registry, arrayProxyItem));
                    }
                    obj[name] = children;
                }
                else
                {
                    obj[name] = JsonValue.Create(value);
                }
            }
        }
        return obj;
    }

    public static string GetJsonPropertyName(this ProxyMetadata metadata, string name, ProxyPropertyMetadata property)
    {
        var attribute = property.Attributes
            .OfType<PropertyAttributeAttribute>()
            .FirstOrDefault();

        if (attribute is not null)
        {
            return GetJsonPropertyName(metadata,
                attribute.PropertyName,
                metadata.Properties[attribute.PropertyName]) + "@" + attribute.AttributeName;
        }

        // TODO: correcly apply JSON naming policy from serializer options
        return name[0].ToString().ToLowerInvariant() + name[1..];
    }
}
