using Namotion.Interceptor;

namespace Namotion.Interception.Lifecycle.Abstractions;

public record struct ProxyPropertyChanged(
    PropertyReference Property,
    object? OldValue,
    object? NewValue,
    IInterceptorCollection Context)
{
}
