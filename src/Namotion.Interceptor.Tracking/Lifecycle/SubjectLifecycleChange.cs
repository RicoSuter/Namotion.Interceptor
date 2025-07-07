namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <param name="Subject">Gets the subject where a property reference pointing to it has been changed.</param>
/// <param name="Property">Gets the property which has been changed.</param>
/// <param name="Index">Gets the index defining the place of the subject in the property's dictionary or collection.</param>
/// <param name="ReferenceCount">Gets the number of properties pointing to the referenced subject.</param>
public record struct SubjectLifecycleChange(
    IInterceptorSubject Subject,
    PropertyReference? Property,
    object? Index,
    int ReferenceCount)
{
}