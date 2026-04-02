using global::NuGet.Versioning;
using Namotion.NuGet.Plugins;

using Namotion.NuGet.Plugins.Configuration;
using Namotion.NuGet.Plugins.Loading;
using Namotion.NuGet.Plugins.Repository;
using Namotion.NuGet.Plugins.Resolution;

namespace Namotion.NuGet.Plugins.Tests.Resolution;

internal class FakeDependencyInfoProvider : IDependencyInfoProvider
{
    private readonly Dictionary<string, (string Name, string Version, (string Id, string VersionRange)[] Deps)> _packages;

    public FakeDependencyInfoProvider(
        Dictionary<string, (string Name, string Version, (string Id, string VersionRange)[] Deps)> packages)
    {
        _packages = packages;
    }

    public Task<IReadOnlyList<(string PackageId, VersionRange VersionRange)>> GetDependenciesAsync(
        string packageName, string version, CancellationToken cancellationToken)
    {
        var key = $"{packageName}/{version}";
        if (_packages.TryGetValue(key, out var info))
        {
            IReadOnlyList<(string, VersionRange)> result = info.Deps
                .Select(dependency => (dependency.Id, VersionRange.Parse(dependency.VersionRange)))
                .ToList();
            return Task.FromResult(result);
        }
        throw new PackageNotFoundException(packageName, version);
    }

    public Task<NuGetVersion?> ResolveVersionAsync(
        string packageName, VersionRange range, CancellationToken cancellationToken)
    {
        var best = _packages.Values
            .Where(package => package.Name.Equals(packageName, StringComparison.OrdinalIgnoreCase))
            .Select(package => NuGetVersion.Parse(package.Version))
            .Where(version => range.Satisfies(version))
            .OrderByDescending(version => version)
            .FirstOrDefault();
        return Task.FromResult(best);
    }
}
