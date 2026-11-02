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
    /// interceptor holds its lock, with each kept subject one of whose keys or positions changed.
    /// Not called when there is none. A subject the write attached is not reported, since its
    /// attach carries its index.
    /// </summary>
    /// <remarks>
    /// A subject the value holds more than once is reported when any of its kept occurrences moved,
    /// and a key that is equal to the old one but another instance may count as moved, so a handler
    /// that keeps every occurrence reads them from the property's value. The handler must not block
    /// and must not write the property that was written. The span is only valid during the call.
    /// </remarks>
    /// <param name="property">The property that was written.</param>
    /// <param name="children">The kept subjects with their key or position in the new value, each once.</param>
    void HandleChildIndexChanges(PropertyReference property, ReadOnlySpan<SubjectChild> children);
}
