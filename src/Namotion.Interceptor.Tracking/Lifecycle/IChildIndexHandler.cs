namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// A handler that keeps the keys or positions of the children of a structural property and has to be told when a
/// write changes those of a child without raising a detach or an attach for it. The handler is registered in the
/// subject context.
/// </summary>
public interface IChildIndexHandler
{
    /// <summary>
    /// Called after a structural write, while the lifecycle interceptor holds its lock, with each subject the write kept
    /// whose keys or positions changed in number, value or order. A subject the write attached is not passed: its attach
    /// carries all of its keys or positions (<see cref="SubjectLifecycleChange.Index"/> and
    /// <see cref="SubjectLifecycleChange.AdditionalIndices"/>). Not called when there is none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The span holds every key or position of each of these subjects in the new value, in order, as one consecutive
    /// group. Enumerate it with <see cref="SubjectChildExtensions.GroupBySubject"/>, and for each group replace what is
    /// stored for the group's subject and the property with the group's children. A subject that is not passed keeps
    /// its entries.
    /// </para>
    /// <para>
    /// The handler must not block, must not throw, and must not write the property that was written:
    /// a throwing handler skips the handlers after it. The span is only valid during the call.
    /// </para>
    /// </remarks>
    /// <param name="property">The property whose value holds the subjects.</param>
    /// <param name="children">The kept subjects whose keys or positions changed, with every key or position they have in the new value.</param>
    void ReplaceChildIndices(PropertyReference property, ReadOnlySpan<SubjectChild> children);
}
