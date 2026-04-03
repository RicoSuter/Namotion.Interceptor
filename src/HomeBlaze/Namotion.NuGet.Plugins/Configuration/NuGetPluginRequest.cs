namespace Namotion.NuGet.Plugins.Configuration;

/// <summary>
/// A request to load a NuGet package as a plugin.
/// </summary>
/// <param name="PackageName">The NuGet package identifier.</param>
/// <param name="Version">The package version to load, or null for latest.</param>
public record NuGetPluginRequest(string PackageName, string? Version = null);
