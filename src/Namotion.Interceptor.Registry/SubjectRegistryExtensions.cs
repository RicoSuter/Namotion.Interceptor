using System.Linq.Expressions;
using System.Numerics;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Registry;

public static class SubjectRegistryExtensions
{
    internal const string SubjectIdKey = "Namotion.Interceptor.SubjectId";

    public static string GenerateSubjectId()
    {
        const string chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
        var value = new BigInteger(Guid.NewGuid().ToByteArray(), isUnsigned: true);
        var result = new char[22];
        for (var i = 21; i >= 0; i--)
        {
            value = BigInteger.DivRem(value, 62, out var remainder);
            result[i] = chars[(int)remainder];
        }
        return new string(result);
    }

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
            foreach (var property in innerSubject.Properties)
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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RegisteredSubjectProperty? TryGetRegisteredProperty(this PropertyReference propertyReference)
    {
        return propertyReference.Subject
            .TryGetRegisteredSubject()?
            .TryGetProperty(propertyReference.Name);
    }
    
    /// <summary>
    /// Gets a registered property by name.
    /// </summary>
    /// <param name="propertyReference">The property to find.</param>
    /// <returns>The registered property.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

    /// <summary>
    /// Gets or lazily generates a stable subject ID for the given subject.
    /// The ID is stored in the subject's Data dictionary and auto-registered
    /// in the registry's reverse index if available.
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <returns>The stable subject ID.</returns>
    public static string GetOrAddSubjectId(this IInterceptorSubject subject)
    {
        return (string)subject.Data.GetOrAdd(
            (null, SubjectIdKey),
            static (_, s) =>
            {
                var id = GenerateSubjectId();
                s.Context.TryGetService<ISubjectRegistry>()?.RegisterSubjectId(id, s);
                return id;
            },
            subject)!;
    }

    /// <summary>
    /// Assigns a known stable ID to a subject (from an incoming update).
    /// Auto-registers in the reverse index if available.
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <param name="id">The stable ID to assign.</param>
    public static void SetSubjectId(this IInterceptorSubject subject, string id)
    {
        var registry = subject.Context.TryGetService<ISubjectRegistry>();

        // Unregister old ID from the reverse index if the subject already has a different one.
        if (subject.Data.TryGetValue((null, SubjectIdKey), out var existingId) &&
            existingId is string oldId && oldId != id)
        {
            registry?.UnregisterSubjectId(oldId);
        }

        subject.Data[(null, SubjectIdKey)] = id;
        registry?.RegisterSubjectId(id, subject);
    }
}
