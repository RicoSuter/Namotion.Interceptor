using Namotion.Interceptor;

namespace Namotion.Proxy.Sources.Abstractions;

public interface ISourcePathProvider
{
    string? TryGetSourcePropertyName(PropertyReference property);

    string? TryGetSourcePath(PropertyReference property);
}
