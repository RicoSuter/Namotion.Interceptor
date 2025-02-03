namespace Namotion.Interceptor.Tracking.Lifecycle;

public record struct LifecycleContext(
    IInterceptorSubject Subject,
    PropertyReference? Property,
    object? Index,
    int ReferenceCount)
{
}
