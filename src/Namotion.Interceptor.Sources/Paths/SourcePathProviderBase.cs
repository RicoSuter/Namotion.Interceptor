using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.Sources.Paths;

public abstract class SourcePathProviderBase : ISourcePathProvider
{
    /// <inheritdoc />
    public virtual bool IsPropertyIncluded(RegisteredSubjectProperty property)
    {
        return true;
    }

    /// <inheritdoc />
    public virtual string? TryGetPropertyName(RegisteredSubjectProperty property)
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
    public virtual IEnumerable<(string path, object? index)> ParsePathSegments(string path)
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
    public RegisteredSubjectProperty? TryGetAttributeFromSegment(RegisteredSubjectProperty property, string segment)
    {
        return property.Parent.Properties
            .SingleOrDefault(p => p.Value.Attributes
                .OfType<PropertyAttributeAttribute>()
                .Any(a => a.PropertyName == property.Property.Name && a.AttributeName == segment))
            .Value;
    }
    
    /// <inheritdoc />
    public virtual RegisteredSubjectProperty? TryGetPropertyFromSegment(RegisteredSubject subject, string segment)
    {
        // TODO(1, perf): Improve performance by caching the property name

        return subject
            .Properties
            .SingleOrDefault(p => TryGetPropertyName(p.Value) == segment)
            .Value;
    }
}