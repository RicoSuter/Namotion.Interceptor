using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Sources.Paths;

public interface ISourcePathProvider
{
    bool IsIncluded(RegisteredSubjectProperty property);
    
    string? TryGetSourcePathSegmentName(RegisteredSubjectProperty property);

    string? TryGetSourcePropertyPath(string path, RegisteredSubjectProperty property);
}
