namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// A lifecycle handler that is called when a subject is attached/assigned ore detached/removed from the subject tree.
/// The handler can be registered in the subject context and applies to the subject and all its children.
/// A subject can also implement this interface directly to handle its own lifecycle changes.
/// </summary>
public interface ILifecycleHandler
{
    /// <summary>
    /// Called when a subject is attached to the subject tree.
    /// </summary>
    /// <param name="change">The lifecycle change information.</param>
    public void AttachSubject(SubjectLifecycleChange change);

    /// <summary>
    /// Called when a subject is detached from the subject tree.
    /// </summary>
    /// <param name="change">The lifecycle change information.</param>
    public void DetachSubject(SubjectLifecycleChange change);
}