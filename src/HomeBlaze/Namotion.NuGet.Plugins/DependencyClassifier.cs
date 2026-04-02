using Namotion.NuGet.Plugins.Configuration;

namespace Namotion.NuGet.Plugins;

/// <summary>
/// Specifies how a dependency is loaded relative to the host application.
/// </summary>
public enum DependencyClassification
{
    /// <summary>
    /// Loaded into the default (host) assembly context, shared across all plugins.
    /// </summary>
    Host,

    /// <summary>
    /// A top-level plugin package loaded into its own assembly context.
    /// </summary>
    Plugin,

    /// <summary>
    /// A transitive dependency loaded into the owning plugin's isolated assembly context.
    /// </summary>
    PluginPrivate
}

/// <summary>
/// Classifies dependencies as host, plugin (top-level), or plugin-private based on
/// host dependency information, glob patterns, and the configured plugin list.
/// </summary>
public class DependencyClassifier
{
    private readonly HostDependencyResolver _hostResolver;
    private readonly IReadOnlyList<string> _hostPackagePatterns;
    private readonly HashSet<string> _configuredPlugins;

    /// <summary>
    /// Initializes a new instance of the <see cref="DependencyClassifier"/> class.
    /// </summary>
    public DependencyClassifier(
        HostDependencyResolver hostResolver,
        IReadOnlyList<string> hostPackagePatterns,
        IEnumerable<string> configuredPluginNames)
    {
        _hostResolver = hostResolver;
        _hostPackagePatterns = hostPackagePatterns;
        _configuredPlugins = new HashSet<string>(configuredPluginNames, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Classifies a single dependency. Priority: configured plugin, then host deps.json, then host patterns, then plugin-private.
    /// </summary>
    public DependencyClassification Classify(string packageName, global::NuGet.Versioning.NuGetVersion version)
    {
        // 1. Configured plugin always wins
        if (_configuredPlugins.Contains(packageName))
        {
            return DependencyClassification.Plugin;
        }

        // 2. In host deps (from deps.json)
        if (_hostResolver.Contains(packageName))
        {
            return DependencyClassification.Host;
        }

        // 3. Matches a host package pattern
        if (PackageNameMatcher.IsMatchAny(packageName, _hostPackagePatterns))
        {
            return DependencyClassification.Host;
        }

        // 4. Everything else is plugin-private
        return DependencyClassification.PluginPrivate;
    }

    /// <summary>
    /// Classifies all dependencies in the given dictionary.
    /// </summary>
    public Dictionary<string, DependencyClassification> ClassifyAll(
        IReadOnlyDictionary<string, global::NuGet.Versioning.NuGetVersion> dependencies)
    {
        return dependencies.ToDictionary(
            kvp => kvp.Key,
            kvp => Classify(kvp.Key, kvp.Value),
            StringComparer.OrdinalIgnoreCase);
    }
}
