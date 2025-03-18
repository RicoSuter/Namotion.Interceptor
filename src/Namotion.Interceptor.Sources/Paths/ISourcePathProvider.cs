using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Sources.Paths;

public interface ISourcePathProvider
{
    bool IsPropertyIncluded(RegisteredSubjectProperty property);

    /// <summary>
    /// Parses the full path into property segments.
    /// </summary>
    /// <param name="path">The path to parse.</param>
    /// <returns>The segments.</returns>
    public IEnumerable<(string path, object? index, bool isAttribute)> ParsePathSegments(string path)
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
                    return (segmentParts[0], index, i > 0);
                }));
    } 
    
    string? TryGetPropertySegmentName(RegisteredSubjectProperty property);

    string GetPropertyAttributePath(string path, RegisteredSubjectProperty attribute);
    
    string GetPropertyPath(string path, RegisteredSubjectProperty property);
}
