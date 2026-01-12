using System.Text.Json.Serialization;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Updates;

/// <summary>
/// Represents an update containing one or more subject property updates.
/// Uses a flat structure where all subjects are stored in a dictionary
/// and referenced by string IDs.
/// </summary>
public class SubjectUpdate
{
    /// <summary>
    /// The ID of the root subject in the <see cref="Subjects"/> dictionary.
    /// </summary>
    [JsonPropertyName("root")]
    public string Root { get; set; } = string.Empty;

    /// <summary>
    /// Dictionary of all subjects keyed by their string ID.
    /// Each subject is a dictionary of property name to property update.
    /// </summary>
    [JsonPropertyName("subjects")]
    public Dictionary<string, Dictionary<string, SubjectPropertyUpdate>> Subjects { get; set; } = new();

    /// <summary>
    /// Creates a complete update with all objects and properties for the given subject as root.
    /// </summary>
    /// <param name="subject">The root subject.</param>
    /// <param name="processors">The update processors to filter and transform updates.</param>
    /// <returns>The update.</returns>
    public static SubjectUpdate CreateCompleteUpdate(
        IInterceptorSubject subject,
        ReadOnlySpan<ISubjectUpdateProcessor> processors)
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
