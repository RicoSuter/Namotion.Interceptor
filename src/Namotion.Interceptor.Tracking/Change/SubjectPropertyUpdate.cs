namespace Namotion.Interceptor.Tracking.Change;

public record struct SubjectPropertyUpdate(
    PropertyReference Property,
    object? OldValue,
    object? NewValue)
{
}
