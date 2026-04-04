namespace Namotion.NuGet.Plugins;

/// <summary>
/// Thrown when plugin dependencies have incompatible version requirements with the host or each other.
/// </summary>
public class NuGetPluginVersionConflictException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NuGetPluginVersionConflictException"/> class.
    /// </summary>
    public NuGetPluginVersionConflictException(IReadOnlyList<NuGetPluginConflict> conflicts)
        : base(FormatMessage(conflicts))
    {
        Conflicts = conflicts;
    }

    /// <summary>
    /// Gets the list of version conflicts that caused this exception.
    /// </summary>
    public IReadOnlyList<NuGetPluginConflict> Conflicts { get; }

    private static string FormatMessage(IReadOnlyList<NuGetPluginConflict> conflicts)
    {
        var lines = conflicts.Select(c => c switch
        {
            NuGetPluginHostConflict host =>
                $"  - {host.PackageName}: plugin '{host.PluginName}' requires {host.RequiredVersion}, host has {host.HostVersion}",
            NuGetPluginRangeConflict range =>
                $"  - {range.PackageName}: incompatible ranges from {string.Join(", ", range.PluginRanges.Select(r => $"'{r.PluginName}' ({r.VersionRange})"))}",
            _ => $"  - {c.PackageName}: unknown conflict"
        });
        return $"Plugin version conflicts detected:\n{string.Join("\n", lines)}";
    }
}
