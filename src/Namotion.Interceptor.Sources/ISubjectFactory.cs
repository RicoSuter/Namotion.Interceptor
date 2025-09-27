using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Sources;

/// <summary>
/// Used to create missing child subjects or subject collections when a property is accessed.
/// </summary>
public interface ISubjectFactory
{
    /// <summary>
    /// Creates a subject for the specified type using an optional service provider.
    /// </summary>
    /// <param name="type">The subject type.</param>
    /// <param name="serviceProvider">The service provider.</param>
    /// <returns>The created subject.</returns>
    IInterceptorSubject CreateSubject(Type type, IServiceProvider? serviceProvider);

    /// <summary>
    /// Creates a subject collection for the specified property and children.
    /// </summary>
    /// <param name="propertyType">The property type.</param>
    /// <param name="children">The initial list of child subjects.</param>
    /// <returns>The created subject collection.</returns>
    IEnumerable<IInterceptorSubject?> CreateSubjectCollection(Type propertyType, params IEnumerable<IInterceptorSubject?> children);
}