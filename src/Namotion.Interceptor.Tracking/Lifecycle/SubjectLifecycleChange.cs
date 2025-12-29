namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Represents a lifecycle change event for a subject in the object graph.
/// </summary>
/// <param name="Subject">The subject being attached or detached.</param>
/// <param name="Property">The property through which the subject is referenced, or null for context-only changes.</param>
/// <param name="Index">The index in a collection/dictionary, or null for direct property references.</param>
/// <param name="ReferenceCount">Number of property references to this subject after this change.</param>
/// <param name="IsFirstAttach">True only on the first attachment (use to initialize resources).</param>
/// <param name="IsLastDetach">True only on the final detachment (use to cleanup resources).</param>
public record struct SubjectLifecycleChange(
    IInterceptorSubject Subject,
    PropertyReference? Property,
    object? Index,
    int ReferenceCount,
    bool IsFirstAttach,
    bool IsLastDetach)
{
}