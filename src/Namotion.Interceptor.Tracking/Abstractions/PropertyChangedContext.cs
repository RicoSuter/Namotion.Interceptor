using Namotion.Interceptor;

namespace Namotion.Interception.Lifecycle.Abstractions;

public record struct PropertyChangedContext(
    PropertyReference Property,
    object? OldValue,
    object? NewValue)
{
}
