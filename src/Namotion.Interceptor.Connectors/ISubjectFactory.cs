using System.Collections;

namespace Namotion.Interceptor.Connectors;

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

    /// <summary>
    /// Creates a subject dictionary for the specified property type and entries.
    /// </summary>
    /// <param name="propertyType">The property type.</param>
    /// <param name="entries">The dictionary entries (key to subject mappings).</param>
    /// <returns>The created subject dictionary.</returns>
    IDictionary CreateSubjectDictionary(Type propertyType, IDictionary<object, IInterceptorSubject> entries);
}
