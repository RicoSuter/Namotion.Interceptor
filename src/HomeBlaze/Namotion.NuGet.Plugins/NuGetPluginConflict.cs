namespace Namotion.NuGet.Plugins;

/// <summary>
/// Describes a version conflict between a plugin's dependency and the host or another plugin.
/// </summary>
public class NuGetPluginConflict
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NuGetPluginConflict"/> class.
    /// </summary>
    public NuGetPluginConflict(
        string assemblyName,
        string requiredVersion,
        string availableVersion,
        string requestedBy)
    {
        AssemblyName = assemblyName;
        RequiredVersion = requiredVersion;
        AvailableVersion = availableVersion;
        RequestedBy = requestedBy;
    }

    /// <summary>
    /// Gets the name of the conflicting assembly/package.
    /// </summary>
    public string AssemblyName { get; }

    /// <summary>
    /// Gets the version required by the plugin.
    /// </summary>
    public string RequiredVersion { get; }

    /// <summary>
    /// Gets the version available in the host or from another plugin.
    /// </summary>
    public string AvailableVersion { get; }

    /// <summary>
    /// Gets the name of the plugin(s) that requested this dependency.
    /// </summary>
    public string RequestedBy { get; }
}
