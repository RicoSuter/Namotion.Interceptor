namespace Namotion.Interceptor.Tracking.Change;

public record struct SubjectPropertyChange(
    PropertyReference Property,
    IReadOnlyDictionary<string, object?> PropertyDataSnapshot,
    DateTimeOffset Timestamp,
    object? OldValue,
    object? NewValue)
{
    public bool TryGetPropertySnapshotData(string key, out object? value)
    {
        return PropertyDataSnapshot.TryGetValue($"{Property.Name}:{key}", out value)!;
    }
}
