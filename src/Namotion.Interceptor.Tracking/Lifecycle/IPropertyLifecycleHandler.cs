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
    /// Called after a collection property write has been fully reconciled
    /// (all detach/attach events processed). Allows handlers to refresh
    /// child index metadata from the live collection value.
    /// </summary>
    void HandleCollectionPropertyChanged(PropertyReference property) { }
}