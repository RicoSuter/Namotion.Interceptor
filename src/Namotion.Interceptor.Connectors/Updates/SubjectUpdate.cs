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
    /// The ID the sender gave the root subject of this update.
    /// </summary>
    /// <remarks>
    /// Set on every update this library builds, complete and partial alike, and it is a mapping hint
    /// rather than an identity assignment: the receiver resolves it to its own root subject for the
    /// duration of one apply, and the local root keeps its own ID. A partial update whose root has no
    /// changed properties of its own still carries it, and then names an ID that is absent from
    /// <see cref="Subjects"/>: without it a subject that references the sender's root, such as a
    /// parent pointer, resolves against nothing in the receiver's registry and the reference is lost
    /// for good, because the sender considers the state delivered and never resends it.
    /// </remarks>
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
    /// Set of subject IDs that contain complete state in this update. The applier must not create a
    /// subject for an ID outside this set, because that would produce a default-valued instance the
    /// sender never resends complete state for, so it can never converge.
    /// </summary>
    /// <remarks>
    /// <c>null</c> means ALL subjects in the update are complete, and only a complete update may say
    /// so. A partial update always carries the set, empty included: a partial update whose only
    /// structural change is a reorder or a removal introduces no new subject and marks nothing
    /// complete, and it has to state that explicitly rather than fall back on the null shorthand.
    /// </remarks>
    [JsonPropertyName("completeSubjectIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HashSet<string>? CompleteSubjectIds { get; init; }

    /// <summary>
    /// Creates an empty update.
    /// </summary>
    public SubjectUpdate()
    {
    }

    /// <summary>
    /// Copies every field of <paramref name="source"/>, so that a derived type carrying additional
    /// transport fields, such as the WebSocket update payload, can be built from an update without
    /// restating its fields at the call site.
    /// </summary>
    /// <remarks>
    /// Every field declared above must be copied here. A field left out disappears from every message
    /// a derived type sends, on the primary data path and without any error, so add new fields to this
    /// constructor in the same edit that declares them. The dictionaries and sets are shared with
    /// <paramref name="source"/> rather than cloned: an update is built, sent and discarded, and no
    /// receiver of a copy mutates them.
    /// </remarks>
    /// <param name="source">The update to copy.</param>
    protected SubjectUpdate(SubjectUpdate source)
    {
        ArgumentNullException.ThrowIfNull(source);

        Root = source.Root;
        Subjects = source.Subjects;
        CompleteSubjectIds = source.CompleteSubjectIds;
    }

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
