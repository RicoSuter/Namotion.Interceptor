using NuGet.Versioning;

namespace Namotion.NuGet.Plugins.Resolution;

/// <summary>
/// Abstraction for fetching package dependency metadata. Allows testing without NuGet API calls.
/// </summary>
internal interface IDependencyInfoProvider
{
    Task<IReadOnlyList<(string PackageId, VersionRange VersionRange)>> GetDependenciesAsync(
        string packageName, string version, CancellationToken cancellationToken);

    Task<NuGetVersion?> ResolveVersionAsync(
        string packageName, VersionRange range, CancellationToken cancellationToken);
}
