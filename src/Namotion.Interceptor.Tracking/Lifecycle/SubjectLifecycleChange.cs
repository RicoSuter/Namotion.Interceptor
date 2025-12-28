namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <param name="Subject">Gets the subject where a property reference pointing to it has been changed.</param>
/// <param name="Property">Gets the property which has been changed.</param>
/// <param name="Index">Gets the index defining the place of the subject in the property's dictionary or collection.</param>
/// <param name="ReferenceCount">Gets the number of properties pointing to the referenced subject.</param>
/// <param name="IsFirstAttach">True when the subject is being attached to the lifecycle system for the first time (whether via context or property).</param>
/// <param name="IsLastDetach">True when the subject is being fully detached from the lifecycle system (no more references).</param>
public record struct SubjectLifecycleChange(
    IInterceptorSubject Subject,
    PropertyReference? Property,
    object? Index,
    int ReferenceCount,
    bool IsFirstAttach,
    bool IsLastDetach)
{
}