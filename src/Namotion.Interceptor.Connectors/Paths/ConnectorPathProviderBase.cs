using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Connectors.Paths;

public abstract class ConnectorPathProviderBase : IConnectorPathProvider
{
    /// <inheritdoc />
    public virtual bool IsPropertyIncluded(RegisteredSubjectProperty property)
    {
        return true;
    }

    /// <inheritdoc />
    public virtual string? TryGetPropertySegment(RegisteredSubjectProperty property)
    {
        return property.BrowseName;
    }

    /// <inheritdoc />
    public virtual string GetPropertyFullPath(IEnumerable<(RegisteredSubjectProperty property, object? index)> propertiesInPath)
    {
        return propertiesInPath.Aggregate("",
            (path, tuple) => (string.IsNullOrEmpty(path) ? "" : path + ".") + tuple.property.BrowseName + (tuple.index is not null ? $"[{tuple.index}]" : ""));
    }

    /// <inheritdoc />
    public virtual IEnumerable<(string segment, object? index)> ParsePathSegments(string path)
    {
        return path
            .Split('.')
            .Where(p => !string.IsNullOrEmpty(p))
            .Select((ss, i) =>
            {
                var segmentParts = ss.Split('[', ']');
                object? index = segmentParts.Length >= 2 ?
                    (int.TryParse(segmentParts[1], out var intIndex) ?
                        intIndex : segmentParts[1]) : null;
                return (segmentParts[0], index);
            });
    }

    /// <inheritdoc />
    public RegisteredSubjectProperty? TryGetAttributeFromSegment(RegisteredSubjectProperty property, string attributeSegment)
    {
        return property.Parent.Properties
            .Where(p => p.IsAttribute)
            .SingleOrDefault(p => p.AttributeMetadata.PropertyName == property.Name &&
                                  p.AttributeMetadata.AttributeName == attributeSegment);
    }

    /// <inheritdoc />
    public virtual RegisteredSubjectProperty? TryGetPropertyFromSegment(RegisteredSubject subject, string propertySegment)
    {
        // TODO(1, perf): Improve performance by caching the property name

        return subject
            .Properties
            .SingleOrDefault(p => TryGetPropertySegment(p) == propertySegment);
    }
}
