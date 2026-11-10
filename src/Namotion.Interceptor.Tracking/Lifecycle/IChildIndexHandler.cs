namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// A handler that keeps the key or position of a child and has to be told when a structural write
/// keeps the child in the property under other ones, since neither a detach nor an attach is
/// raised for it. The handler is registered in the subject context.
/// </summary>
public interface IChildIndexHandler
{
    /// <summary>
    /// Called after a structural write, while the lifecycle interceptor holds its lock, with each kept subject whose
    /// keys or positions changed in number, value or order, and each subject the write attached at more than one of
    /// them. Not called when there is none. A subject attached at one key or position is not reported, since its
    /// attach carries it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The span holds every key or position of each reported subject in the new value, in order, as one consecutive
    /// run. Replace the entries stored for the subject and the property with the run; a subject that is not reported
    /// keeps its entries.
    /// </para>
    /// <para>
    /// The handler must not block, must not throw, and must not write the property that was written:
    /// a throwing handler skips the handlers after it. The span is only valid during the call.
    /// </para>
    /// </remarks>
    /// <param name="property">The property that was written.</param>
    /// <param name="children">The reported subjects with every key or position they have in the new value.</param>
    void HandleChildIndexChanges(PropertyReference property, ReadOnlySpan<SubjectChild> children);
}
