namespace Namotion.Interceptor.Connectors.Updates;

/// <summary>
/// Specifies how a <see cref="SubjectPropertyUpdate"/> relates to what the receiver already holds.
/// A mode that does not apply to the update's <see cref="SubjectPropertyUpdate.Kind"/> is treated as
/// <see cref="Incremental"/>.
/// </summary>
public enum SubjectPropertyUpdateMode
{
    /// <summary>
    /// The update applies on top of what the receiver holds: a path entry, sparse items, or a diff
    /// with operations. Default value, omitted on the wire.
    /// </summary>
    Incremental = 0,

    /// <summary>
    /// For <see cref="SubjectPropertyUpdateKind.Collection"/> and <see cref="SubjectPropertyUpdateKind.Dictionary"/>
    /// updates only: <see cref="SubjectPropertyUpdate.Items"/> state the whole membership, so a member
    /// they do not list is removed. Unlisted child properties of a retained member are kept. An update
    /// with operations, or whose items do not name every position or key exactly once with a payload,
    /// applies as <see cref="Incremental"/>.
    /// </summary>
    Complete,

    /// <summary>
    /// For <see cref="SubjectPropertyUpdateKind.Object"/> updates only: the reference now holds a
    /// different subject than before, so the payload never applies to the subject the receiver
    /// currently holds there.
    /// </summary>
    Replaced
}
