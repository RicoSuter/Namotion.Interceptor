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
    /// Gets or sets an optional predicate that determines whether a package should be loaded into the host (default) assembly context.
    /// When null, no additional packages are treated as host packages (automatic discovery via plugin.json and assembly attributes still applies).
    /// </summary>
    public Func<string, bool>? IsHostPackage { get; set; }

    /// <summary>
    /// Gets or sets the host dependency resolver for version validation against the host application.
    /// </summary>
    public HostDependencyResolver? HostDependencies { get; set; }

    /// <summary>
    /// Gets or sets the directory for caching extracted packages. If null, a temporary directory is
    /// created per loader instance and cleaned up on disposal. When using multiple loader instances
    /// in the same process, sharing a single cache directory is recommended to avoid redundant downloads.
    /// </summary>
    public string? CacheDirectory { get; set; }

    /// <summary>
    /// Gets or sets whether pre-release package versions should be considered. Defaults to false.
    /// </summary>
    public bool IncludePrerelease { get; set; }

    /// <summary>
    /// Gets or sets the host identifier for matching assembly-level host package attributes.
    /// When null, assembly attribute discovery is skipped entirely.
    /// When set, only packages whose [assembly: AssemblyMetadata("Namotion.NuGet.Plugins.HostPackage", "...")] value
    /// matches this identifier (case-insensitive) are treated as host-shared.
    /// </summary>
    public string? HostIdentifier { get; set; }
}
