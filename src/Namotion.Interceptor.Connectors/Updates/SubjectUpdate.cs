using System.Text.Json.Serialization;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Updates;

/// <summary>
/// Represents an update for a subject with its property updates.
/// </summary>
public class SubjectUpdate
{
    /// <summary>
    /// Gets or sets the unique ID of the subject (only set if there is a reference pointing to it).
    /// </summary>
    public int? Id { get; set; }

    /// <summary>
    /// Gets or sets the reference ID of an already existing subject.
    /// </summary>
    public int? Reference { get; set; }

    /// <summary>
    /// Gets a dictionary of property updates.
    /// The dictionary is mutable so that additional updates can be attached.
    /// </summary>
    public Dictionary<string, SubjectPropertyUpdate> Properties { get; init; } = new();

    /// <summary>
    /// Gets or sets custom extension data added by the transformPropertyUpdate function.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, object>? ExtensionData { get; set; }

    /// <summary>
    /// Creates a complete update with all objects and properties for the given subject as root.
    /// </summary>
    /// <param name="subject">The root subject.</param>
    /// <param name="processors">The update processors to filter and transform updates.</param>
    /// <returns>The update.</returns>
    public static SubjectUpdate CreateCompleteUpdate(IInterceptorSubject subject, ReadOnlySpan<ISubjectUpdateProcessor> processors)
        => SubjectUpdateFactory.CreateComplete(subject, processors);

    /// <summary>
    /// Creates a partial update from the given property changes.
    /// Only directly or indirectly needed objects and properties are added.
    /// </summary>
    /// <param name="subject">The root subject.</param>
    /// <param name="propertyChanges">The changes to look up within the object graph.</param>
    /// <param name="processors">The update processors to filter and transform updates.</param>
    /// <returns>The update.</returns>
    public static SubjectUpdate CreatePartialUpdateFromChanges(
        IInterceptorSubject subject,
        ReadOnlySpan<SubjectPropertyChange> propertyChanges,
        ReadOnlySpan<ISubjectUpdateProcessor> processors)
        => SubjectUpdateFactory.CreatePartialFromChanges(subject, propertyChanges, processors);
}
