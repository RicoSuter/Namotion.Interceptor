namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// A lifecycle handler that is called when a subject enters or leaves the object graph.
/// The handler can be registered in the subject context and applies to the subject and all its children.
/// A subject can also implement this interface directly to handle its own lifecycle changes.
/// </summary>
public interface ILifecycleHandler
{
    /// <summary>
    /// Called when a subject first enters the object graph.
    /// </summary>
    /// <param name="change">The lifecycle change information.</param>
    void OnSubjectAttached(SubjectLifecycleChange change);

    /// <summary>
    /// Called when a subject fully leaves the object graph.
    /// </summary>
    /// <param name="change">The lifecycle change information.</param>
    void OnSubjectDetached(SubjectLifecycleChange change);
}
