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
    IEnumerable<(string path, bool isAttribute)> ParsePathSegments(string path);
    
    string? TryGetPropertySegmentName(RegisteredSubjectProperty property);

    string GetPropertyAttributePath(string path, RegisteredSubjectProperty attribute);
    
    string GetPropertyPath(string path, RegisteredSubjectProperty property);
}
