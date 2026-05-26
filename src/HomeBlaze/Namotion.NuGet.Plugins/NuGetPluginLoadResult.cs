using Namotion.NuGet.Plugins.Loading;

namespace Namotion.NuGet.Plugins;

/// <summary>
/// Result of a plugin loading operation, containing loaded plugins and any failures.
/// </summary>
public record NuGetPluginLoadResult(
    IReadOnlyList<NuGetPlugin> LoadedPlugins,
    IReadOnlyList<NuGetPluginFailure> Failures)
{
    /// <summary>
    /// Gets whether all plugins loaded successfully (no failures).
    /// </summary>
    public bool Success => Failures.Count == 0;
}
