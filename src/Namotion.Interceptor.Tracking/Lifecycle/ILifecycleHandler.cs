namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// A lifecycle handler that is called when a subject enters/leaves the object graph
/// and when property references are added/removed.
/// </summary>
public interface ILifecycleHandler
{
    /// <summary>
    /// Called when a lifecycle event occurs for a subject.
    /// Check the <see cref="SubjectLifecycleChange.IsContextAttach"/>, <see cref="SubjectLifecycleChange.IsPropertyReferenceAdded"/>,
    /// <see cref="SubjectLifecycleChange.IsPropertyReferenceRemoved"/>, and <see cref="SubjectLifecycleChange.IsContextDetach"/>
    /// flags to determine which events occurred.
    /// </summary>
    void HandleLifecycleChange(SubjectLifecycleChange change);
}