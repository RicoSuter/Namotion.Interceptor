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
    /// interceptor holds its lock, with each kept subject whose key or position may have changed.
    /// Not called when there is none.
    /// </summary>
    /// <remarks>
    /// The handler must not block, and must not write the property that was written. The span is
    /// only valid during the call.
    /// </remarks>
    /// <param name="property">The property that was written.</param>
    /// <param name="children">The kept subjects with their key or position in the new value, each once.</param>
    void HandleChildIndexChanges(PropertyReference property, ReadOnlySpan<SubjectChildIndex> children);
}
