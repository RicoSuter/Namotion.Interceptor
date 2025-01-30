using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.Registry;

public static class ProxyRegistryExtensions
{
    public static IEnumerable<RegisteredProxyProperty> GetProperties(this IProxyRegistry registry)
    {
        foreach (var pair in registry.KnownProxies)
        {
            foreach (var property in pair.Value.Properties.Values)
            {
                yield return property;
            }
        }
    }

    public static RegisteredProxyProperty? TryGetProperty(this IReadOnlyDictionary<IInterceptorSubject, RegisteredProxy> properties, PropertyReference property)
    {
        if (properties.TryGetValue(property.Subject, out var metadata))
        {
            if (metadata.Properties.TryGetValue(property.Name, out var result))
            {
                return result;
            }
        }

        return null;
    }

    public static RegisteredProxyProperty? TryGetPropertyAttribute(this PropertyReference property, string attributeName)
    {
        // TODO: Also support non-registry scenario

        var context = property.Subject.Context as IServiceProvider;
        var registry = context?.GetRequiredService<IProxyRegistry>() 
                       ?? throw new InvalidOperationException($"The {nameof(IProxyRegistry)} is missing.");
        
        var attribute = registry.KnownProxies[property.Subject].Properties
            .SingleOrDefault(p => p.Value.Attributes
                .OfType<PropertyAttributeAttribute>()
                .Any(a => a.PropertyName == property.Name && a.AttributeName == attributeName));

        return attribute.Value;
    }

    public static IEnumerable<KeyValuePair<string, RegisteredProxyProperty>> GetPropertyAttributes(this PropertyReference property)
    {
        // TODO: Also support non-registry scenario

        var context = property.Subject.Context as IServiceProvider;
        var registry = context?.GetRequiredService<IProxyRegistry>()
                       ?? throw new InvalidOperationException($"The {nameof(IProxyRegistry)} is missing.");

        return registry.KnownProxies[property.Subject].Properties
            .Where(p => p.Value.Attributes
                .OfType<PropertyAttributeAttribute>()
                .Any(a => a.PropertyName == property.Name));
    }
}
