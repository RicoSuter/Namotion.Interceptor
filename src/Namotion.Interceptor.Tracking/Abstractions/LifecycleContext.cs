using Namotion.Interceptor;

namespace Namotion.Interception.Lifecycle.Abstractions;

public record struct LifecycleContext(
    PropertyReference? Property,
    object? Index,
    IInterceptorSubject Subject,
    int ReferenceCount)
{
}
