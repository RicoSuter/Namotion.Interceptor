using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Connectors.Updates;

/// <summary>
/// Processes and transforms <see cref="SubjectUpdate"/> instances during creation.
/// Implementations can filter properties and apply transformations such as
/// property name casing changes or custom attribute modifications.
/// </summary>
public interface ISubjectUpdateProcessor
{
    /// <summary>
    /// Determines whether a property should be included in the update.
    /// Called for each property during update creation.
    /// </summary>
    /// <param name="property">The registered property to evaluate.</param>
    /// <returns>True if the property should be included; false to exclude it from the update.</returns>
    public bool IsIncluded(RegisteredSubjectProperty property) => true;

    /// <summary>
    /// Transforms the complete <see cref="SubjectUpdate"/> after all properties have been processed.
    /// Use this for transformations that need access to all subjects, such as renaming
    /// property keys across the entire update (e.g., camelCase conversion).
    /// </summary>
    /// <param name="subject">The root subject of the update.</param>
    /// <param name="update">The update to transform.</param>
    /// <returns>The transformed update.</returns>
    public SubjectUpdate TransformSubjectUpdate(IInterceptorSubject subject, SubjectUpdate update) => update;

    /// <summary>
    /// Transforms an individual <see cref="SubjectPropertyUpdate"/> after it has been created.
    /// Use this for property-specific transformations such as modifying attributes
    /// or converting attribute names (e.g., camelCase conversion).
    /// </summary>
    /// <param name="property">The registered property associated with this update.</param>
    /// <param name="update">The property update to transform.</param>
    /// <returns>The transformed property update.</returns>
    public SubjectPropertyUpdate TransformSubjectPropertyUpdate(RegisteredSubjectProperty property, SubjectPropertyUpdate update) => update;
}
