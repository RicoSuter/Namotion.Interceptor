namespace Namotion.Interceptor.Sources;

public interface ISourcePathProvider
{
    string? TryGetSourcePropertyName(PropertyReference property);

    string? TryGetSourcePath(PropertyReference property);
}
