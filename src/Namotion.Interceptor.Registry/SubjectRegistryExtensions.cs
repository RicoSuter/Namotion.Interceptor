using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Registry.Abstractions;

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
            if (!registeredSubjects.Add(innerSubject))
            {
                yield break;
            }

            // TODO(perf): Implement directly on subject to avoid accessing Properties property
            foreach (var property in innerSubject.PropertiesAndAttributes)
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
        registry ??= subject.Context.GetService<ISubjectRegistry>();
        return registry
            .TryGetRegisteredSubject(subject)?
            .TryGetProperty(propertyName);
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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RegisteredSubject? TryGetRegisteredSubject(this IInterceptorSubject subject)
    {
        // TODO(perf): Replace calls of TryGetRegisteredSubject with registry.TryGetRegisteredSubject to avoid multiple registry resolves
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
}
