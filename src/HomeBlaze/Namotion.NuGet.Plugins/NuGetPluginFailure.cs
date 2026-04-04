namespace Namotion.NuGet.Plugins;

/// <summary>
/// Describes a plugin that failed to load, including the reason and optional conflict/exception details.
/// </summary>
public record NuGetPluginFailure(
    string PackageName,
    string Reason,
    IReadOnlyList<NuGetPluginConflict>? Conflicts = null,
    Exception? Exception = null);
