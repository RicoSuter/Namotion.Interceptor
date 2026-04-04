namespace Namotion.NuGet.Plugins;

/// <summary>
/// Describes a dependency of a loaded plugin and how it was classified.
/// </summary>
public record NuGetPluginDependencyInfo
{
    /// <summary>
    /// Gets the NuGet package name.
    /// </summary>
    public required string PackageName { get; init; }

    /// <summary>
    /// Gets the resolved version string.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    /// Gets how this dependency was classified (Host, Plugin, or PluginPrivate).
    /// </summary>
    public required DependencyClassification Classification { get; init; }
}
