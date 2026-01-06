namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// A lifecycle handler that is called when a subject's own property is attached or detached from tracking.
/// The handler can be registered in the subject context and applies to the subject and all its children.
/// A subject can also implement this interface directly to handle its own property lifecycle changes.
/// </summary>
public interface IPropertyLifecycleHandler
{
    /// <summary>
    /// Called after a subject's property is attached to tracking.
    /// </summary>
    /// <param name="change">The property lifecycle change information.</param>
    void OnPropertyAttached(SubjectPropertyLifecycleChange change);

    /// <summary>
    /// Called after a subject's property is detached from tracking.
    /// </summary>
    /// <param name="change">The property lifecycle change information.</param>
    void OnPropertyDetached(SubjectPropertyLifecycleChange change);
}
