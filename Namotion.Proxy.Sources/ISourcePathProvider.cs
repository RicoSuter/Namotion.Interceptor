namespace Namotion.Proxy.Sources;

public interface ISourcePathProvider
{
    string? TryGetSourceProperty(ProxyPropertyReference property);

    string? TryGetSourcePath(ProxyPropertyReference property);
}
