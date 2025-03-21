namespace Namotion.Interceptor.Tracking.Lifecycle;

public record struct SubjectLifecycleUpdate(
    IInterceptorSubject Subject,
    PropertyReference? Property,
    object? Index,
    int ReferenceCount)
{
}
