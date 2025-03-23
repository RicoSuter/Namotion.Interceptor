using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Sources;

/// <summary>
/// Used to create missing child subjects or subject collections when a property is accessed.
/// </summary>
public interface ISubjectFactory
{
    /// <summary>
    /// Creates a subject for the specified property and index.
    /// </summary>
    /// <param name="property">The property.</param>
    /// <param name="index">The optional index.</param>
    /// <returns>The created subject.</returns>
    IInterceptorSubject CreateSubject(RegisteredSubjectProperty property, object? index);

    /// <summary>
    /// Creates a subject collection for the specified property and children.
    /// </summary>
    /// <param name="property">The property.</param>
    /// <param name="children">The initial list of child subjects.</param>
    /// <returns>The created subject collection.</returns>
    ICollection<IInterceptorSubject?> CreateSubjectCollection(RegisteredSubjectProperty property, params IEnumerable<IInterceptorSubject?> children);
}