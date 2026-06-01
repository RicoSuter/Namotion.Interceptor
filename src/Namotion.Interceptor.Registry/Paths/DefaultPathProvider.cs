namespace Namotion.Interceptor.Registry.Paths;

/// <summary>
/// Default path provider that uses property BrowseNames as path segments.
/// </summary>
public class DefaultPathProvider : PathProviderBase
{
    private readonly char _pathSeparator;

    /// <summary>
    /// Gets the singleton instance of the default path provider (separator '.').
    /// </summary>
    public static DefaultPathProvider Instance { get; } = new();

    private DefaultPathProvider()
        : this('.')
    {
    }

    /// <summary>Creates a default path provider with the given separator.</summary>
    public DefaultPathProvider(char pathSeparator)
    {
        _pathSeparator = pathSeparator;
    }

    /// <inheritdoc />
    public override char PathSeparator => _pathSeparator;
}
