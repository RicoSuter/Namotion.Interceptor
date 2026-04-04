using Namotion.NuGet.Plugins.Configuration;

namespace Namotion.NuGet.Plugins;

/// <summary>
/// Validates semantic version compatibility between plugin requirements and host availability.
/// Rules: major must match exactly, plugin minor &lt;= host minor, patch ignored.
/// </summary>
public static class VersionCompatibility
{
    /// <summary>
    /// Checks if a required version is compatible with an available version.
    /// </summary>
    public static bool IsCompatible(global::NuGet.Versioning.NuGetVersion required, global::NuGet.Versioning.NuGetVersion available)
    {
        if (required.Major != available.Major)
        {
            return false;
        }

        if (required.Minor > available.Minor)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Finds all version conflicts between plugin dependencies and host dependencies.
    /// Only checks dependencies that exist in the host (non-host deps are not conflicts).
    /// </summary>
    public static IReadOnlyList<NuGetPluginHostConflict> FindConflicts(
        IReadOnlyDictionary<string, global::NuGet.Versioning.NuGetVersion> pluginDependencies,
        HostDependencyResolver hostResolver,
        string pluginName)
    {
        var conflicts = new List<NuGetPluginHostConflict>();

        foreach (var (packageName, requiredVersion) in pluginDependencies)
        {
            var hostVersion = hostResolver.GetVersion(packageName);
            if (hostVersion != null && !IsCompatible(requiredVersion, hostVersion))
            {
                conflicts.Add(new NuGetPluginHostConflict(
                    packageName,
                    requiredVersion.ToNormalizedString(),
                    hostVersion.ToNormalizedString(),
                    pluginName));
            }
        }

        return conflicts;
    }
}
