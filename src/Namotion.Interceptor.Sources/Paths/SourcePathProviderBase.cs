using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Sources.Paths;

public abstract class SourcePathProviderBase : ISourcePathProvider
{
    public virtual bool IsPropertyIncluded(RegisteredSubjectProperty property)
    {
        return true;
    }

    public virtual string? TryGetPropertyName(RegisteredSubjectProperty property)
    {
        return property.BrowseName;
    }

    public virtual string GetPropertyFullPath(string path, RegisteredSubjectProperty property)
    {
        return path + property.BrowseName;
    }

    /// <inheritdoc />
    public virtual IEnumerable<(string path, object? index)> ParsePathSegments(string path)
    {
        return path
            .Split('.')
            .SelectMany(s => s
                .Split('.')
                .Select((ss, i) =>
                {
                    var segmentParts = ss.Split('[', ']');
                    object? index = segmentParts.Length >= 2 ? 
                        (int.TryParse(segmentParts[1], out var intIndex) ? 
                            intIndex : segmentParts[1]) : null;
                    return (segmentParts[0], index);
                }));
    } 
    
    /// <inheritdoc />
    public virtual RegisteredSubjectProperty? TryGetPropertyFromSegment(RegisteredSubject subject, string segment)
    {
        // TODO(perf): Improve performance by caching the property name
        return subject
            .Properties
            .SingleOrDefault(p => TryGetPropertyName(p.Value) == segment)
            .Value;
    }
}