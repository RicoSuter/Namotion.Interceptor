namespace Namotion.Interceptor.Connectors.Updates;

/// <summary>
/// Specifies the kind of property update in a <see cref="SubjectPropertyUpdate"/>.
/// </summary>
public enum SubjectPropertyUpdateKind
{
    /// <summary>
    /// No update kind specified. Default value.
    /// </summary>
    None,

    /// <summary>
    /// A primitive or simple value update.
    /// Uses <see cref="SubjectPropertyUpdate.Value"/> to store the value.
    /// </summary>
    Value,

    /// <summary>
    /// A single subject reference update.
    /// Uses <see cref="SubjectPropertyUpdate.Id"/> to reference the subject by ID.
    /// A null Id means the reference is null.
    /// </summary>
    Object,

    /// <summary>
    /// An index-based collection (array or list) of subjects.
    /// Uses <see cref="SubjectPropertyUpdate.Operations"/> for structural changes (Insert, Remove, Move),
    /// <see cref="SubjectPropertyUpdate.Items"/> for sparse item updates by index,
    /// and <see cref="SubjectPropertyUpdate.Count"/> for the total count after operations.
    /// </summary>
    Collection,

    /// <summary>
    /// A key-based dictionary of subjects.
    /// Uses <see cref="SubjectPropertyUpdate.Operations"/> for structural changes (Insert, Remove),
    /// <see cref="SubjectPropertyUpdate.Items"/> for sparse item updates by key,
    /// and <see cref="SubjectPropertyUpdate.Count"/> for the total count after operations.
    /// Note: Move operations are not applicable to dictionaries.
    /// </summary>
    Dictionary
}
