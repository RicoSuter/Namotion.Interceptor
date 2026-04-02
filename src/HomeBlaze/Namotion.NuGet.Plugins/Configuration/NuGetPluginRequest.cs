namespace Namotion.NuGet.Plugins.Configuration;

/// <summary>
/// A request to load a NuGet package as a plugin, optionally from a local file path.
/// </summary>
/// <param name="PackageName">The NuGet package identifier.</param>
/// <param name="Version">The package version to load, or null for latest.</param>
/// <param name="Path">A local .nupkg file path, or null to download from feeds.</param>
public record NuGetPluginRequest(string PackageName, string? Version = null, string? Path = null);
