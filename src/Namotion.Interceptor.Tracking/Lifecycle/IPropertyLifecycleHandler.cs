namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// A property lifecycle handler that is called when a property is attached/detached from the subject tree
/// or when a collection property's children have changed.
/// The handler can be registered in the subject context and applies to the subject and all its children.
/// A subject can also implement this interface directly to handle its own property lifecycle changes.
/// </summary>
public interface IPropertyLifecycleHandler
{
    /// <summary>
    /// Called when a property is attached to the subject tree.
    /// </summary>
    /// <param name="change">The lifecycle change information.</param>
    public void AttachProperty(SubjectPropertyLifecycleChange change);

    /// <summary>
    /// Called when a property is detached from the subject tree.
    /// </summary>
    /// <param name="change">The lifecycle change information.</param>
    public void DetachProperty(SubjectPropertyLifecycleChange change);

    /// <summary>
    /// Called after a subject-container property write has been fully reconciled
    /// (all detach/attach events processed) and at least one subject was retained,
    /// so that handlers can refresh the child index metadata the write moved.
    /// </summary>
    /// <param name="property">The property reference.</param>
    /// <param name="children">
    /// Every child subject the new value holds, in enumeration order, with the index it is now held at,
    /// newly attached children included. Each entry's own property is always the one passed in.
    /// A subject held at several indices appears once per index; the first occurrence is the one attach
    /// recorded, so handlers must apply it and ignore the rest.
    /// The span is backed by a pooled buffer and is only valid for the duration of the call, so a handler
    /// which keeps any of it must copy it. Being a ref struct, the span itself cannot be stored.
    /// </param>
    void RefreshChildIndices(PropertyReference property, ReadOnlySpan<SubjectChildReference> children) { }
}