using System.Text.Json.Serialization;
using Namotion.Interceptor.Connectors.Updates.Internal;
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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("root")]
    public string? Root { get; init; }

    /// <summary>
    /// Dictionary of all subjects keyed by their string ID.
    /// Each subject is a dictionary of property name to property update.
    /// </summary>
    [JsonPropertyName("subjects")]
    public Dictionary<string, Dictionary<string, SubjectPropertyUpdate>> Subjects { get; init; } = new();

    /// <summary>
    /// Set of subject IDs that contain complete state in this update.
    /// <c>null</c> means ALL subjects are complete (e.g., a full initial-state update).
    /// Non-null means only the listed IDs have complete state; others are references
    /// to subjects that should already exist on the receiver. The applier must not
    /// create new subject instances for IDs not in this set — doing so would produce
    /// subjects with default values that corrupt state.
    /// </summary>
    [JsonPropertyName("completeSubjectIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HashSet<string>? CompleteSubjectIds { get; init; }

    /// <summary>
    /// Creates a complete update with all objects and properties for the given subject as root.
    /// </summary>
    /// <param name="subject">The root subject.</param>
    /// <param name="processors">The update processors to filter and transform updates.</param>
    /// <returns>The update.</returns>
    public static SubjectUpdate CreateCompleteUpdate(
        IInterceptorSubject subject,
        ISubjectUpdateProcessor[] processors)
        => SubjectUpdateFactory.CreateCompleteUpdate(subject, processors);

    /// <summary>
    /// Creates a partial update from the given property changes.
    /// Only directly or indirectly necessary objects and properties are added.
    /// </summary>
    /// <param name="subject">The root subject.</param>
    /// <param name="propertyChanges">The changes to look up within the object graph.</param>
    /// <param name="processors">The update processors to filter and transform updates.</param>
    /// <returns>The update.</returns>
    public static SubjectUpdate CreatePartialUpdateFromChanges(
        IInterceptorSubject subject,
        ReadOnlySpan<SubjectPropertyChange> propertyChanges,
        ISubjectUpdateProcessor[] processors)
        => SubjectUpdateFactory.CreatePartialUpdateFromChanges(subject, propertyChanges, processors);
}
