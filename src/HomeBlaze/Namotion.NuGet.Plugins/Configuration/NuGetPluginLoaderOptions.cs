namespace Namotion.NuGet.Plugins.Configuration;

/// <summary>
/// Configuration options for <see cref="Loading.NuGetPluginLoader"/>.
/// </summary>
public class NuGetPluginLoaderOptions
{
    /// <summary>
    /// Gets or sets the NuGet feeds to search for packages. Defaults to nuget.org.
    /// </summary>
    public IReadOnlyList<NuGetFeed> Feeds { get; set; } = [NuGetFeed.NuGetOrg];

    /// <summary>
    /// Gets or sets glob patterns for packages that should be loaded into the host (default) assembly context.
    /// </summary>
    public IReadOnlyList<string> HostPackages { get; set; } = [];

    /// <summary>
    /// Gets or sets the host dependency resolver for version validation against the host application.
    /// </summary>
    public HostDependencyResolver? HostDependencies { get; set; }

    /// <summary>
    /// Gets or sets the directory for caching extracted packages. If null, a temporary directory is used.
    /// </summary>
    public string? CacheDirectory { get; set; }
}
