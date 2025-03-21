using System.Linq.Expressions;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.Registry;

public static class SubjectRegistryExtensions
{
    /// <summary>
    /// Gets all registered properties of the subject and child subjects.
    /// </summary>
    /// <param name="subject">The root subject.</param>
    /// <returns>The update.</returns>
    public static IEnumerable<RegisteredSubjectProperty> GetRegisteredProperties(this RegisteredSubject subject)
    {
        // TODO: Avoid endless recursion
        
        foreach (var (_, property) in subject.Properties)
        {
            yield return property;
                
            foreach (var childProperty in property.Children
                         .Select(c => c.Subject.TryGetRegisteredSubject())
                         .Where(s => s is not null)
                         .SelectMany(s => s!.GetRegisteredProperties()))
            {
                yield return childProperty;
            }
        }
    }
    
    public static RegisteredSubjectProperty? TryGetRegisteredProperty(this IInterceptorSubject subject, string propertyName, ISubjectRegistry? registry = null)
    {
        registry = registry ?? subject.Context.GetService<ISubjectRegistry>();
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
        return propertyReference.Subject
            .TryGetRegisteredSubject()?
            .TryGetProperty(propertyReference.Name) 
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
