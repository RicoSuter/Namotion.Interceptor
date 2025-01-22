namespace Namotion.Interceptor.Tracking.Abstractions;

public record struct LifecycleContext(
    PropertyReference? Property,
    object? Index,
    IInterceptorSubject Subject,
    int ReferenceCount)
{
}
