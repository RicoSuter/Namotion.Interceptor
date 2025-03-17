using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Sources.Paths;

public interface ISourcePathProvider
{
    bool IsIncluded(RegisteredSubjectProperty property);
    
    string? TryGetSourcePathSegmentName(RegisteredSubjectProperty property);

    // string GetSourcePropertyPath(property, path) => path;
    string? TryGetSourcePropertyPath(string path, RegisteredSubjectProperty property);
    
    string GetAttributePath(RegisteredSubjectProperty property, RegisteredSubjectProperty attribute, string pathHint);
    
    string GetPropertyPath(RegisteredSubjectProperty property, string pathHint);
}
