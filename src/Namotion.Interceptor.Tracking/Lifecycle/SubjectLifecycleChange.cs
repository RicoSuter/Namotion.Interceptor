namespace Namotion.Interceptor.Tracking.Lifecycle;

public record struct SubjectLifecycleChange(
    IInterceptorSubject Subject,
    PropertyReference? Property,
    object? Index,
    int ReferenceCount)
{
}
