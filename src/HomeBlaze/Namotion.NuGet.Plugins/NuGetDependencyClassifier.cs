using Namotion.NuGet.Plugins.Configuration;

namespace Namotion.NuGet.Plugins;

/// <summary>
/// Specifies how a dependency is loaded relative to the host application.
/// </summary>
public enum NuGetDependencyClassification
{
    /// <summary>
    /// Loaded into the default (host) assembly context, shared across all plugins.
    /// </summary>
    Host,

    /// <summary>
    /// The entry-point plugin package loaded into its own assembly context.
    /// </summary>
    Entry,

    /// <summary>
    /// A transitive dependency loaded into the owning plugin's isolated assembly context.
    /// </summary>
    Isolated
}

/// <summary>
/// Classifies dependencies as host, entry (top-level), or isolated based on
/// host dependency information, a host package predicate, discovered host-shared packages,
/// and the configured plugin list.
/// </summary>
internal class NuGetDependencyClassifier
{
    private readonly HostDependencyResolver _hostResolver;
    private readonly Func<string, bool>? _isHostPackage;
    private readonly HashSet<string> _configuredPlugins;
    private readonly HashSet<string> _discoveredHostShared;

    /// <summary>
    /// Initializes a new instance of the <see cref="NuGetDependencyClassifier"/> class.
    /// </summary>
    public NuGetDependencyClassifier(
        HostDependencyResolver hostResolver,
        Func<string, bool>? isHostPackage,
        IEnumerable<string> configuredPluginNames,
        ISet<string>? discoveredHostShared = null)
    {
        _hostResolver = hostResolver;
        _isHostPackage = isHostPackage;
        _configuredPlugins = new HashSet<string>(configuredPluginNames, StringComparer.OrdinalIgnoreCase);
        _discoveredHostShared = discoveredHostShared != null
            ? new HashSet<string>(discoveredHostShared, StringComparer.OrdinalIgnoreCase)
            : [];
    }

    /// <summary>
    /// Classifies a single dependency. Priority: configured plugin, then host deps.json,
    /// then host patterns, then discovered host-shared, then isolated.
    /// </summary>
    public NuGetDependencyClassification Classify(string packageName)
    {
        // 1. Configured plugin always wins
        if (_configuredPlugins.Contains(packageName))
        {
            return NuGetDependencyClassification.Entry;
        }

        // 2. In host deps (from deps.json)
        if (_hostResolver.Contains(packageName))
        {
            return NuGetDependencyClassification.Host;
        }

        // 3. Matches the host package predicate
        if (_isHostPackage?.Invoke(packageName) == true)
        {
            return NuGetDependencyClassification.Host;
        }

        // 4. Discovered via HostShared attribute or plugin.json hostDependencies
        if (_discoveredHostShared.Contains(packageName))
        {
            return NuGetDependencyClassification.Host;
        }

        // 5. Everything else is isolated
        return NuGetDependencyClassification.Isolated;
    }

    /// <summary>
    /// Classifies all dependencies in the given dictionary.
    /// </summary>
    public Dictionary<string, NuGetDependencyClassification> ClassifyAll(
        IReadOnlyDictionary<string, global::NuGet.Versioning.NuGetVersion> dependencies)
    {
        return dependencies.ToDictionary(
            kvp => kvp.Key,
            kvp => Classify(kvp.Key),
            StringComparer.OrdinalIgnoreCase);
    }
}