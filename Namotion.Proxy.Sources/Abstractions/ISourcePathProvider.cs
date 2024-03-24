namespace Namotion.Proxy.Sources.Abstractions;

public interface ISourcePathProvider
{
    string? TryGetSourceProperty(ProxyPropertyReference property);

    string? TryGetSourcePath(ProxyPropertyReference property);
}
