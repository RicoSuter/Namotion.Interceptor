namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// A property lifecycle handler that is called when a property is attached to or detached from the subject tree,
/// or when a collection property's immutable child projection changes.
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
    /// Legacy source-compatibility hook for callers that explicitly provide a live collection
    /// value. The built-in lifecycle does not invoke this overload because doing so after
    /// publication would require retaining or re-enumerating mutable user state.
    /// </summary>
    /// <param name="property">The collection property reference.</param>
    /// <param name="value">The collection value supplied by the explicit caller.</param>
    void RefreshCollectionProperty(PropertyReference property, object? value) { }

    /// <summary>
    /// Called with the complete immutable property projection after retained collection occurrences change.
    /// </summary>
    /// <param name="change">The revisioned property projection.</param>
    void RefreshCollectionProperty(SubjectPropertyLifecycleChange change) { }

}
