namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// A lifecycle handler that is called when a subject enters/leaves the object graph
/// and when property references are added/removed.
/// </summary>
/// <remarks>
/// The change struct contains bool flags indicating which events occurred:
/// - IsAttached: Subject first entered the graph
/// - IsReferenceAdded: Property reference was added
/// - IsReferenceRemoved: Property reference was removed
/// - IsDetached: Subject is leaving the graph
///
/// Multiple flags can be true simultaneously (e.g., IsAttached + IsReferenceAdded on first attach via property).
/// </remarks>
public interface ILifecycleHandler
{
    /// <summary>
    /// Called when a lifecycle event occurs for a subject.
    /// Check the IsAttached, IsReferenceAdded, IsReferenceRemoved, and IsDetached flags
    /// to determine which events occurred.
    /// </summary>
    void OnLifecycleEvent(SubjectLifecycleChange change);
}