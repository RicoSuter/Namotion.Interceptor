namespace Namotion.Interceptor.Tracking.Abstractions;

public record struct PropertyChangedContext(
    PropertyReference Property,
    object? OldValue,
    object? NewValue)
{
}
