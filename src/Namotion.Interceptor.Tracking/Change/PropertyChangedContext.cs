namespace Namotion.Interceptor.Tracking.Change;

public record struct PropertyChangedContext(
    PropertyReference Property,
    object? OldValue,
    object? NewValue)
{
}
