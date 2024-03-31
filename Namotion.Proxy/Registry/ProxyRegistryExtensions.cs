using Namotion.Proxy.Registry.Abstractions;
using Namotion.Proxy.Registry.Attributes;

using System.Collections;
using System.Text.Json;
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

    public static ProxyPropertyMetadata? TryGetAttribute(this IProxy proxy, string propertyName, string attributeName)
    {
        var registry = proxy.Context?.GetHandler<IProxyRegistry>() 
            ?? throw new InvalidOperationException($"The {nameof(IProxyRegistry)} is missing.");
        
        var attribute = registry?.KnownProxies[proxy].Properties
            .SingleOrDefault(p => p.Value.Attributes
                .OfType<PropertyAttributeAttribute>()
                .Any(a => a.PropertyName == propertyName && a.AttributeName == attributeName));

        return attribute?.Value;
    }

    public static JsonObject ToJsonObject(this IProxy proxy)
    {
        var registry = proxy.Context?.GetHandler<IProxyRegistry>()
            ?? throw new InvalidOperationException($"The {nameof(IProxyRegistry)} is missing.");

        var obj = new JsonObject();
        if (registry.KnownProxies.TryGetValue(proxy, out var metadata))
        {
            foreach (var property in metadata
                .Properties
                .Where(p => p.Value.GetValue is not null))
            {
                var propertyName = property.GetJsonPropertyName();
                var value = property.Value.GetValue?.Invoke();
                if (value is IProxy childProxy)
                {
                    obj[propertyName] = childProxy.ToJsonObject();
                }
                else if (value is ICollection collection && collection.OfType<IProxy>().Any())
                {
                    var children = new JsonArray();
                    foreach (var arrayProxyItem in collection.OfType<IProxy>())
                    {
                        children.Add(arrayProxyItem.ToJsonObject());
                    }
                    obj[propertyName] = children;
                }
                else
                {
                    obj[propertyName] = JsonValue.Create(value);
                }
            }
        }
        return obj;
    }

    public static string GetJsonPropertyName(this KeyValuePair<string, ProxyPropertyMetadata> property)
    {
        var attribute = property
            .Value
            .Attributes
            .OfType<PropertyAttributeAttribute>()
            .FirstOrDefault();

        if (attribute is not null)
        {
            return property
                .Value
                .Parent
                .Properties
                .Single(p => p.Key == attribute.PropertyName) // TODO: Improve performance??
                .GetJsonPropertyName() + "@" + attribute.AttributeName;
        }

        return JsonNamingPolicy.CamelCase.ConvertName(property.Key);
    }
}
