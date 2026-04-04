using Namotion.NuGet.Plugins.Loading;

namespace Namotion.NuGet.Plugins;

/// <summary>
/// Result of a plugin loading operation, containing loaded plugins and any failures.
/// </summary>
public class NuGetPluginLoadResult
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NuGetPluginLoadResult"/> class.
    /// </summary>
    public NuGetPluginLoadResult(
        IReadOnlyList<NuGetPlugin> loadedPlugins,
        IReadOnlyList<NuGetPluginFailure> failures)
    {
        LoadedPlugins = loadedPlugins;
        Failures = failures;
    }

    /// <summary>
    /// Gets whether all plugins loaded successfully (no failures).
    /// </summary>
    public bool Success => Failures.Count == 0;

    /// <summary>
    /// Gets the successfully loaded plugins.
    /// </summary>
    public IReadOnlyList<NuGetPlugin> LoadedPlugins { get; }

    /// <summary>
    /// Gets the list of plugin loading failures.
    /// </summary>
    public IReadOnlyList<NuGetPluginFailure> Failures { get; }
}
