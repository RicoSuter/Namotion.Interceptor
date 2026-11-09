namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// A handler that keeps the key or position of a child and has to be told when a structural write
/// keeps the child in the property under another one, since neither a detach nor an attach is
/// raised for it. The handler is registered in the subject context.
/// </summary>
public interface IChildIndexHandler
{
    /// <summary>
    /// Called after a structural write that kept subjects in the property, while the lifecycle
    /// interceptor holds its lock, with each kept subject whose key or position changed. A subject held more than once
    /// also counts as changed when the write only adds, removes, replaces or reorders its later keys or positions.
    /// Not called when there is none. A subject the write attached is not reported, since its
    /// attach carries its index.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A reported index can equal the stored one, also as another instance, such as for a subject whose later keys
    /// changed. An equal key counts as unchanged, so the handler keeps the instance it stored.
    /// </para>
    /// <para>
    /// The handler must not block, must not throw, and must not write the property that was written:
    /// a throwing handler skips the handlers after it. The span is only valid during the call.
    /// </para>
    /// </remarks>
    /// <param name="property">The property that was written.</param>
    /// <param name="children">The kept subjects with their key or position in the new value, each once.</param>
    void HandleChildIndexChanges(PropertyReference property, ReadOnlySpan<SubjectChild> children);
}
