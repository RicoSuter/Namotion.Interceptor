using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.Registry;

public static class SubjectRegistryExtensions
{
    public static IEnumerable<RegisteredSubjectProperty> GetProperties(this ISubjectRegistry registry)
    {
        foreach (var pair in registry.KnownSubjects)
        {
            foreach (var property in pair.Value.Properties.Values)
            {
                yield return property;
            }
        }
    }

    public static RegisteredSubjectProperty? TryGetProperty(this IReadOnlyDictionary<IInterceptorSubject, RegisteredSubject> properties, PropertyReference property)
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

    public static RegisteredSubjectProperty? TryGetPropertyAttribute(this PropertyReference property, string attributeName)
    {
        // TODO: Also support non-registry scenario

        var registry = property.Subject.Context.GetService<ISubjectRegistry>() 
            ?? throw new InvalidOperationException($"The {nameof(ISubjectRegistry)} is missing.");
        
        var attribute = registry.KnownSubjects[property.Subject].Properties
            .SingleOrDefault(p => p.Value.Attributes
                .OfType<PropertyAttributeAttribute>()
                .Any(a => a.PropertyName == property.Name && a.AttributeName == attributeName));

        return attribute.Value;
    }

    public static IEnumerable<KeyValuePair<string, RegisteredSubjectProperty>> GetPropertyAttributes(this PropertyReference property)
    {
        // TODO: Also support non-registry scenario

        var registry = property.Subject.Context.GetService<ISubjectRegistry>() 
            ?? throw new InvalidOperationException($"The {nameof(ISubjectRegistry)} is missing.");

        return registry.KnownSubjects[property.Subject].Properties
            .Where(p => p.Value.Attributes
                .OfType<PropertyAttributeAttribute>()
                .Any(a => a.PropertyName == property.Name));
    }
}
