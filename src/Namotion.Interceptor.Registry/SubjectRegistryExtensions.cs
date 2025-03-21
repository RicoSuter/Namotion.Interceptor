using System.Linq.Expressions;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.Registry;

public static class SubjectRegistryExtensions
{
    public static RegisteredSubjectProperty? TryGetRegisteredProperty(this IReadOnlyDictionary<IInterceptorSubject, RegisteredSubject> properties, PropertyReference property)
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
    
    public static RegisteredSubjectProperty? TryGetRegisteredProperty(this IInterceptorSubject subject, string propertyName)
    {
        var registry = subject.Context.GetService<ISubjectRegistry>();
        return registry.KnownSubjects.TryGetValue(subject, out var registeredSubject)
            ? registeredSubject.Properties.GetValueOrDefault(propertyName)
            : null;
    }
    
    public static RegisteredSubjectProperty? TryGetRegisteredProperty<T>(this T subject, Expression<Func<T, object?>> propertyExpression)
        where T : IInterceptorSubject
    {
        var actualSubject = subject;

        var bodyMemberExpression = propertyExpression.Body as MemberExpression;
        var parentExpression = bodyMemberExpression?.Expression;
        if (parentExpression is not null)
        {
            // Extract the parameter from the original lambda expression
            var originalParameter = propertyExpression.Parameters[0];

            // Create a new lambda using the same parameter
            var compiledParent = Expression
                .Lambda<Func<T, object?>>(parentExpression, originalParameter)
                .Compile()
                .Invoke(subject);
            
            actualSubject = (T?)compiledParent;
        }

        if (actualSubject is not null)
        {
            var propertyName = bodyMemberExpression?.Member.Name;
            return propertyName is not null ? actualSubject.TryGetRegisteredProperty(propertyName) : null;
        }

        return null;
    }
    
    public static RegisteredSubjectProperty GetRegisteredProperty(this PropertyReference propertyReference)
    {
        var registry = propertyReference.Subject.Context.GetService<ISubjectRegistry>();
        return GetRegisteredProperty(propertyReference, registry);
    }

    public static RegisteredSubjectProperty GetRegisteredProperty(this PropertyReference propertyReference, ISubjectRegistry registry)
    {
        return registry.KnownSubjects.TryGetRegisteredProperty(propertyReference) 
               ?? throw new InvalidOperationException($"Property '{propertyReference.Name}' not found.");
    }

    public static RegisteredSubject? TryGetRegisteredSubject(this IInterceptorSubject subject)
    {
        var registry = subject.Context.GetService<ISubjectRegistry>();
        return registry.KnownSubjects.GetValueOrDefault(subject);
    }
    
    public static RegisteredSubjectProperty? TryGetRegisteredAttribute(this PropertyReference property, string attributeName)
    {
        return TryGetRegisteredSubjectProperty(property.Subject, property.Name, attributeName);
    }

    public static RegisteredSubjectProperty GetRegisteredAttribute(this IInterceptorSubject subject, string propertyName, string attributeName)
    {
        return TryGetRegisteredSubjectProperty(subject, propertyName, attributeName)
            ?? throw new InvalidOperationException($"Attribute '{attributeName}' not found on property '{propertyName}'.");
    }

    private static RegisteredSubjectProperty? TryGetRegisteredSubjectProperty(IInterceptorSubject subject, string propertyName, string attributeName)
    {
        var registry = subject.Context.GetService<ISubjectRegistry>();
        var attribute = registry
            .KnownSubjects[subject]
            .Properties
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
