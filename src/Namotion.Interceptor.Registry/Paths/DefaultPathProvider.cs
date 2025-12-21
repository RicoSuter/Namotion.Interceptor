namespace Namotion.Interceptor.Registry.Paths;

/// <summary>
/// Default path provider that uses property BrowseNames as path segments.
/// </summary>
public class DefaultPathProvider : PathProviderBase
{
    /// <summary>
    /// Gets the singleton instance of the default path provider.
    /// </summary>
    public static DefaultPathProvider Instance { get; } = new();

    private DefaultPathProvider()
    {
    }
}
