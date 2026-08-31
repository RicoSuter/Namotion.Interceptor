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
    /// Legacy source-compatibility hook for callers that explicitly provide a live collection
    /// value. The built-in lifecycle does not invoke this overload because doing so after
    /// publication would require retaining or re-enumerating mutable user state. Implement
    /// <see cref="RefreshCollectionProperty(SubjectPropertyLifecycleChange)"/> to observe built-in
    /// collection refreshes through their complete immutable projection.
    /// </summary>
    /// <param name="property">The collection property reference.</param>
    /// <param name="value">The collection value supplied by the explicit caller.</param>
    void RefreshCollectionProperty(PropertyReference property, object? value) { }

    /// <summary>
    /// Called with the complete immutable property projection after collection indices change.
    /// The default is a no-op so implementations of the legacy overload remain source compatible
    /// without receiving a synthesized or stale live value.
    /// </summary>
    /// <param name="change">The revisioned property projection.</param>
    void RefreshCollectionProperty(SubjectPropertyLifecycleChange change) { }
}
