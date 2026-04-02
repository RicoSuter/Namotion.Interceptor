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
        var lines = conflicts.Select(c =>
            $"  - {c.AssemblyName}: requires {c.RequiredVersion}, available {c.AvailableVersion} (requested by {c.RequestedBy})");
        return $"Plugin version conflicts detected:\n{string.Join("\n", lines)}";
    }
}
