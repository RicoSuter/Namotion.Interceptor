namespace Namotion.Interceptor.Tracking.Change;

public record struct SubjectPropertyChange(
    PropertyReference Property,
    object? OldValue,
    object? NewValue)
{
}
