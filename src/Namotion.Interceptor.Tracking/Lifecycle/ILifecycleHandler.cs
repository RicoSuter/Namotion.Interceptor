namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// A lifecycle handler that is called when a subject enters/leaves the object graph
/// and when property references are added/removed.
/// </summary>
public interface ILifecycleHandler
{
    /// <summary>
    /// Called when a lifecycle event occurs for a subject.
    /// Check the IsAttached, IsPropertyReferenceAdded, IsPropertyReferenceRemoved, and IsDetached flags
    /// to determine which events occurred.
    /// </summary>
    void OnLifecycleEvent(SubjectLifecycleChange change);
}