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
    public static IEnumerable<RegisteredSubjectProperty> GetAllProperties(this RegisteredSubject subject)
    {
        foreach (var registeredSubjectProperty in InnerGetAllProperties(subject, []))
        {
            yield return registeredSubjectProperty;
        } 

        IEnumerable<RegisteredSubjectProperty> InnerGetAllProperties(
            RegisteredSubject innerSubject, HashSet<RegisteredSubject> registeredSubjects)
        {
            if (registeredSubjects.Add(innerSubject) == false)
                yield break;
            
            foreach (var (_, property) in innerSubject.Properties)
            {
                yield return property;

                foreach (var childProperty in property.Children
                    .Select(c => c.Subject.TryGetRegisteredSubject())
                    .Where(s => s is not null)
                    .SelectMany(s => InnerGetAllProperties(s!, registeredSubjects)))
                {
                    yield return childProperty;
                }
            }
        }
    }
    
    /// <summary>
    /// Gets a registered property by name.
    /// </summary>
    /// <param name="subject">The subject with the property.</param>
    /// <param name="propertyName">The property name to find.</param>
    /// <param name="registry">The optional registry, otherwise the registry is resolved dynamically.</param>
    /// <returns>The registered property.</returns>
    public static RegisteredSubjectProperty? TryGetRegisteredProperty(this IInterceptorSubject subject, string propertyName, ISubjectRegistry? registry = null)
    {
        registry = registry ?? subject.Context.GetService<ISubjectRegistry>();
        return registry
            .TryGetRegisteredSubject(subject)?
            .Properties
            .GetValueOrDefault(propertyName);
    }
    
    /// <summary>
    /// Gets a registered property by name.
    /// </summary>
    /// <param name="propertyReference">The property to find.</param>
    /// <returns>The registered property.</returns>
    public static RegisteredSubjectProperty GetRegisteredProperty(this PropertyReference propertyReference)
    {
        return propertyReference.Subject
                   .TryGetRegisteredSubject()?
                   .TryGetProperty(propertyReference.Name) 
               ?? throw new InvalidOperationException($"Property '{propertyReference.Name}' not found.");
    }

    /// <summary>
    /// Gets a registered subject.
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <returns>The registered subject.</returns>
    public static RegisteredSubject? TryGetRegisteredSubject(this IInterceptorSubject subject)
    {
        var registry = subject.Context.TryGetService<ISubjectRegistry>();
        return registry?.TryGetRegisteredSubject(subject);
    }
    
    /// <summary>
    /// Gets a registered property from an expression.
    /// </summary>
    /// <param name="subject">The subject with the property.</param>
    /// <param name="propertyExpression">The property expression to find.</param>
    /// <typeparam name="T">The subject type to allow properly typed expression.</typeparam>
    /// <returns>The registered property.</returns>
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
    
    /// <summary>
    /// Gets all registered attributes of a property.
    /// </summary>
    /// <param name="subject">The subject with the property with attributes.</param>
    /// <param name="propertyName">The property with the attributes.</param>
    /// <returns>The dictionary with the attribute names and their registered properties.</returns>
    public static IReadOnlyDictionary<string, RegisteredSubjectProperty> GetRegisteredAttributes(this IInterceptorSubject subject, string propertyName)
    {
        return GetRegisteredAttributes(new PropertyReference(subject, propertyName));
    }

    /// <summary>
    /// Gets all registered attributes of a property.
    /// </summary>
    /// <param name="property">The property with the attributes.</param>
    /// <returns>The dictionary with the attribute names and their registered properties.</returns>
    public static IReadOnlyDictionary<string, RegisteredSubjectProperty> GetRegisteredAttributes(this PropertyReference property)
    {
        // TODO(perf): Cache the property attributes

        var registry = property.Subject.Context.GetService<ISubjectRegistry>();
        return registry
            .TryGetRegisteredSubject(property.Subject)?
            .Properties
            .Where(p => p.Value.ReflectionAttributes
                .OfType<PropertyAttributeAttribute>()
                .Any(a => a.PropertyName == property.Name))
            .ToDictionary()?? [];
    }
    
    /// <summary>
    /// Gets a registered attribute of a property.
    /// </summary>
    /// <param name="property">The property with the attribute.</param>
    /// <param name="attributeName">The attribute name which is attached to the specified property.</param>
    /// <returns>The attribute which is a specialization of a property.</returns>
    public static RegisteredSubjectProperty? TryGetRegisteredAttribute(this PropertyReference property, string attributeName)
    {
        return TryGetRegisteredSubjectProperty(property.Subject, property.Name, attributeName);
    }

    /// <summary>
    /// Gets a registered attribute of a property.
    /// </summary>
    /// <param name="subject">The subject with the property.</param>
    /// <param name="propertyName">The property name.</param>
    /// <param name="attributeName">The attribute name which is attached to the specified property.</param>
    /// <returns>The attribute which is a specialization of a property.</returns>
    public static RegisteredSubjectProperty GetRegisteredAttribute(this IInterceptorSubject subject, string propertyName, string attributeName)
    {
        return TryGetRegisteredSubjectProperty(subject, propertyName, attributeName)
            ?? throw new InvalidOperationException($"Attribute '{attributeName}' not found on property '{propertyName}'.");
    }

    private static RegisteredSubjectProperty? TryGetRegisteredSubjectProperty(IInterceptorSubject subject, string propertyName, string attributeName)
    {
        var registry = subject.Context.GetService<ISubjectRegistry>();
        var attribute = registry
            .TryGetRegisteredSubject(subject)?
            .Properties
            .SingleOrDefault(p => p.Value.ReflectionAttributes
                .OfType<PropertyAttributeAttribute>()
                .Any(a => a.PropertyName == propertyName && a.AttributeName == attributeName));

        return attribute?.Value;
    }
}
