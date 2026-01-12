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
    Item,

    /// <summary>
    /// A collection (array, list, or dictionary) of subjects.
    /// Uses <see cref="SubjectPropertyUpdate.Operations"/> for structural changes (Insert, Remove, Move),
    /// <see cref="SubjectPropertyUpdate.Collection"/> for sparse item updates by index/key,
    /// and <see cref="SubjectPropertyUpdate.Count"/> for the total count after operations.
    /// </summary>
    Collection
}
