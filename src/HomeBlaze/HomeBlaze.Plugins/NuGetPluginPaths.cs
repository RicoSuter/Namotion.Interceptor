namespace HomeBlaze.Plugins;

/// <summary>
/// Resolves the cache folder and feed locations of a plugin provider. Relative values resolve against the
/// instance data directory, or the working directory when there is none; rooted paths and absolute URIs are
/// not combined with the data directory.
/// </summary>
internal static class NuGetPluginPaths
{
    /// <summary>
    /// The cache folder used when none is configured, relative to the data directory.
    /// </summary>
    public static readonly string DefaultCacheDirectory = Path.Combine("Plugins", "Cache");

    /// <summary>
    /// Resolves the plugin cache directory. A null, empty, or whitespace-only <paramref name="configured"/>
    /// value falls back to <see cref="DefaultCacheDirectory"/>.
    /// </summary>
    public static string ResolveCacheDirectory(string? configured, string? dataDirectory)
    {
        var value = string.IsNullOrWhiteSpace(configured) ? DefaultCacheDirectory : configured.Trim();
        return Path.GetFullPath(value, dataDirectory ?? Directory.GetCurrentDirectory());
    }

    /// <summary>
    /// Resolves a plugin feed location. A rooted path or an absolute URI, including a <c>file:</c> URI, is
    /// returned unchanged; anything else is treated as a folder path relative to the data directory.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="url"/> is null, empty, or consists only of whitespace.
    /// </exception>
    public static string ResolveFeedUrl(string url, string? dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var value = url.Trim();
        if (Path.IsPathRooted(value) || Uri.TryCreate(value, UriKind.Absolute, out _))
        {
            return value;
        }

        return Path.GetFullPath(value, dataDirectory ?? Directory.GetCurrentDirectory());
    }
}
