namespace Namotion.Interceptor.Sources.Paths;

public interface ISourcePathProvider
{
    string? TryGetSourcePathSegmentName(PropertyReference property);

    string? TryGetSourcePropertyPath(PropertyReference property);
}
