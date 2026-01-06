namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Represents a lifecycle change event for a subject in the object graph.
/// </summary>
/// <param name="Context">The context used to invoke handlers (parent's context for property attachments).</param>
/// <param name="Subject">The subject being attached or detached.</param>
/// <param name="Property">The property through which the subject is referenced, or null for context-only changes.</param>
/// <param name="Index">The index in a collection/dictionary, or null for direct property references.</param>
/// <param name="ReferenceCount">Number of property references to this subject after this change.</param>
public record struct SubjectLifecycleChange(
    IInterceptorSubjectContext Context,
    IInterceptorSubject Subject,
    PropertyReference? Property,
    object? Index,
    int ReferenceCount)
{
}
