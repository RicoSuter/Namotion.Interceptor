namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// A lifecycle handler that is called when a subject's reference count changes.
/// This is triggered when a subject is added to or removed from a parent property.
/// The handler can be registered in the subject context and applies to the subject and all its children.
/// A subject can also implement this interface directly to handle its own reference changes.
/// </summary>
public interface IReferenceLifecycleHandler
{
    /// <summary>
    /// Called after a subject is attached to a parent property.
    /// </summary>
    /// <param name="change">The lifecycle change information with Property set.</param>
    void OnSubjectAttachedToProperty(SubjectLifecycleChange change);

    /// <summary>
    /// Called after a subject is detached from a parent property.
    /// </summary>
    /// <param name="change">The lifecycle change information with Property set.</param>
    void OnSubjectDetachedFromProperty(SubjectLifecycleChange change);
}
