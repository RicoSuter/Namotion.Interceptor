namespace HomeBlaze.Plugins;

/// <summary>
/// Resolves the folder paths of a plugin provider. Relative paths resolve against the instance data directory,
/// or the working directory when there is none; rooted paths and absolute URIs are used as-is.
/// </summary>
internal static class NuGetPluginPaths
{
    /// <summary>
    /// The cache folder used when none is configured, relative to the data directory.
    /// </summary>
    public static readonly string DefaultCacheDirectory = Path.Combine("Plugins", "Cache");

    public static string ResolveCacheDirectory(string? configured, string? dataDirectory)
    {
        var value = string.IsNullOrWhiteSpace(configured) ? DefaultCacheDirectory : configured.Trim();
        return Path.GetFullPath(value, dataDirectory ?? Directory.GetCurrentDirectory());
    }

    public static string ResolveFeedUrl(string url, string? dataDirectory)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !uri.IsFile)
        {
            return url;
        }

        return Path.IsPathRooted(url)
            ? url
            : Path.GetFullPath(url, dataDirectory ?? Directory.GetCurrentDirectory());
    }
}
