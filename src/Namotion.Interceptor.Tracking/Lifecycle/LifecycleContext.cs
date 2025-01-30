namespace Namotion.Interceptor.Tracking.Lifecycle;

public record struct LifecycleContext(
    PropertyReference? Property,
    object? Index,
    IInterceptorSubject Subject,
    int ReferenceCount)
{
}
