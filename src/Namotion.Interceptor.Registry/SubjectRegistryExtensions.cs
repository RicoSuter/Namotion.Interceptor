using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.Registry;

public static class SubjectRegistryExtensions
{
    public static IEnumerable<RegisteredSubjectProperty> GetSubjectAndChildProperties(this IInterceptorSubject subject)
    {
        var registry = subject.Context.GetService<ISubjectRegistry>();
        if (registry.KnownSubjects.TryGetValue(subject, out var registeredSubject))
        {
            foreach (var property in registeredSubject.Properties.Values)
            {
                yield return property;

                foreach (var child in property.Children
                    .SelectMany(c => GetSubjectAndChildProperties(c.Subject)))
                {
                    yield return child;
                }
            }
        }
    }

    public static IEnumerable<RegisteredSubjectProperty> GetAllProperties(this ISubjectRegistry registry)
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
    
    public static RegisteredSubjectProperty GetRegisteredProperty(this PropertyReference propertyReference)
    {
        var registry = propertyReference.Subject.Context.GetService<ISubjectRegistry>();
        return registry.KnownSubjects.TryGetProperty(propertyReference) 
               ?? throw new InvalidOperationException($"Property '{propertyReference.Name}' not found.");
    }
    
    public static RegisteredSubjectProperty? TryGetRegisteredProperty(this IInterceptorSubject subject, string propertyName)
    {
        var registry = subject.Context.GetService<ISubjectRegistry>();
        return registry.KnownSubjects.TryGetValue(subject, out var registeredSubject)
            ? registeredSubject.Properties.GetValueOrDefault(propertyName)
            : null;
    }
    
    public static RegisteredSubjectProperty? TryGetRegisteredAttribute(this PropertyReference property, string attributeName)
    {
        return TryGetRegisteredAttribute(property.Subject, property.Name, attributeName);
    }

    public static RegisteredSubjectProperty? TryGetRegisteredAttribute(this IInterceptorSubject subject, string propertyName, string attributeName)
    {
        var registry = subject.Context.GetService<ISubjectRegistry>();
        var attribute = registry.KnownSubjects[subject].Properties
            .SingleOrDefault(p => p.Value.Attributes
                .OfType<PropertyAttributeAttribute>()
                .Any(a => a.PropertyName == propertyName && a.AttributeName == attributeName));

        return attribute.Value;
    }

    public static IReadOnlyDictionary<string, RegisteredSubjectProperty> GetRegisteredAttributes(this PropertyReference property)
    {
        var registry = property.Subject.Context.GetService<ISubjectRegistry>();
        return registry.KnownSubjects[property.Subject].Properties
            .Where(p => p.Value.Attributes
                .OfType<PropertyAttributeAttribute>()
                .Any(a => a.PropertyName == property.Name))
            .ToDictionary();
    }
}
