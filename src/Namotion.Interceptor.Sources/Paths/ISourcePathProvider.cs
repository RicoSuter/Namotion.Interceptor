using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Sources.Paths;

public interface ISourcePathProvider
{
    bool IsPropertyIncluded(RegisteredSubjectProperty property);
    
    string? TryGetPropertyName(RegisteredSubjectProperty property);
    
    string GetPropertyFullPath(string path, RegisteredSubjectProperty property);

    /// <summary>
    /// Parses the full path into property segments.
    /// </summary>
    /// <param name="path">The path to parse.</param>
    /// <returns>The segments.</returns>
    IEnumerable<(string path, object? index)> ParsePathSegments(string path);

    RegisteredSubjectProperty? TryGetAttributeFromSegment(RegisteredSubjectProperty property, string segment);
    
    RegisteredSubjectProperty? TryGetPropertyFromSegment(RegisteredSubject subject, string segment);
}
