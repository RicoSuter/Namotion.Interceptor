namespace Namotion.Interceptor.Tracking.Change;

public record struct SubjectPropertyChange(
    PropertyReference Property,
    DateTimeOffset Timestamp,
    object? OldValue,
    object? NewValue)
{
}
