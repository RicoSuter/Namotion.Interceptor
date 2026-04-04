namespace Namotion.NuGet.Plugins;

/// <summary>
/// Base type for plugin version conflicts.
/// </summary>
public abstract record NuGetPluginConflict(string PackageName);

/// <summary>
/// A conflict between a plugin's dependency version and the host's loaded version.
/// </summary>
public record NuGetPluginHostConflict(
    string PackageName,
    string RequiredVersion,
    string HostVersion,
    string PluginName) : NuGetPluginConflict(PackageName);

/// <summary>
/// A conflict where multiple plugins require incompatible version ranges for a shared package.
/// </summary>
public record NuGetPluginRangeConflict(
    string PackageName,
    IReadOnlyList<(string PluginName, string VersionRange)> PluginRanges) : NuGetPluginConflict(PackageName);
