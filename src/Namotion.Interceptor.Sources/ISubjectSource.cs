namespace Namotion.Interceptor.Sources;

public interface ISubjectSource
{
    string? TryGetSourcePropertyPath(PropertyReference property);

    Task<IDisposable?> InitializeAsync(IEnumerable<PropertyPathReference> properties, Action<PropertyPathReference> propertyUpdateAction, CancellationToken cancellationToken);

    // TODO: Should return dict<path, obj?>?
    Task<IEnumerable<PropertyPathReference>> ReadAsync(IEnumerable<PropertyPathReference> properties, CancellationToken cancellationToken);

    Task WriteAsync(IEnumerable<PropertyPathReference> propertyChanges, CancellationToken cancellationToken);
}

public readonly record struct PropertyPathReference(PropertyReference Property, string Path, object? Value);
