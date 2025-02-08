namespace Namotion.Interceptor.Sources;

public interface ISourcePathProvider
{
    string? TryGetSourcePathSegmentName(PropertyReference property);

    string? TryGetSourcePropertyPath(PropertyReference property);
}
