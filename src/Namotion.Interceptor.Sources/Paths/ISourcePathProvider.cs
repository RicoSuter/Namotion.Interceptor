using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Sources.Paths;

public interface ISourcePathProvider
{
    bool IsIncluded(RegisteredSubjectProperty property);
    
    string? TryGetSourcePathSegmentName(RegisteredSubjectProperty property);

    // change to GetP(path, property) and exclude property segment?
    string? TryGetSourcePropertyPath(RegisteredSubjectProperty property, string fullPathHint);
    
    string GetAttributePath(string path, RegisteredSubjectProperty property, RegisteredSubjectProperty attribute);
    
    string GetPropertyPath(string path, RegisteredSubjectProperty property);
}
