namespace Namotion.Interceptor.Tracking.Change;

public record struct SubjectPropertyChange(
    PropertyReference Property,
    object? Source,
    DateTimeOffset Timestamp,
    object? OldValue,
    object? NewValue)
{
}
