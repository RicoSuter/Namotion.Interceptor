using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging.Core;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

using Namotion.NuGet.Plugins.Configuration;

namespace Namotion.NuGet.Plugins.Resolution;

internal class NuGetDependencyInfoProvider : IDependencyInfoProvider
{
    private readonly IReadOnlyList<NuGetFeed> _feeds;
    private readonly NuGetFramework _targetFramework;
    private readonly ILogger _logger;

    public NuGetDependencyInfoProvider(
        IReadOnlyList<NuGetFeed> feeds,
        ILogger? logger = null)
    {
        _feeds = feeds;
        _targetFramework = NuGetFramework.Parse($"net{Environment.Version.Major}.{Environment.Version.Minor}");
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task<IReadOnlyList<(string PackageId, VersionRange VersionRange)>> GetDependenciesAsync(
        string packageName, string version, CancellationToken cancellationToken)
    {
        foreach (var feed in _feeds)
        {
            try
            {
                var sourceRepository = CreateSourceRepository(feed);
                var dependencyInfoResource = await sourceRepository.GetResourceAsync<DependencyInfoResource>(cancellationToken);
                using var dependencyCacheContext = new SourceCacheContext();
                var dependencyInfo = await dependencyInfoResource.ResolvePackage(
                    new PackageIdentity(packageName, NuGetVersion.Parse(version)),
                    _targetFramework, dependencyCacheContext,
                    global::NuGet.Common.NullLogger.Instance, cancellationToken);

                if (dependencyInfo != null)
                {
                    return dependencyInfo.Dependencies
                        .Select(dependency => (dependency.Id, dependency.VersionRange))
                        .ToList();
                }
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "Failed to get deps from {Feed} for {Package}.", feed.Name, packageName);
            }
        }
        return [];
    }

    public async Task<NuGetVersion?> ResolveVersionAsync(
        string packageName, VersionRange range, CancellationToken cancellationToken)
    {
        foreach (var feed in _feeds)
        {
            try
            {
                var sourceRepository = CreateSourceRepository(feed);
                var metadataResource = await sourceRepository.GetResourceAsync<PackageMetadataResource>(cancellationToken);
                using var metadataCacheContext = new SourceCacheContext();
                var packages = await metadataResource.GetMetadataAsync(
                    packageName, includePrerelease: false, includeUnlisted: false,
                    metadataCacheContext, global::NuGet.Common.NullLogger.Instance, cancellationToken);

                var best = packages
                    .Where(package => range.Satisfies(package.Identity.Version))
                    .OrderByDescending(package => package.Identity.Version)
                    .FirstOrDefault();

                if (best != null) return best.Identity.Version;
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "Failed to resolve version from {Feed} for {Package}.", feed.Name, packageName);
            }
        }
        return null;
    }

    private static SourceRepository CreateSourceRepository(NuGetFeed feed)
    {
        var packageSource = new PackageSource(feed.Url);
        if (feed.ApiKey != null)
        {
            packageSource.Credentials = new PackageSourceCredential(
                feed.Url, "apikey", feed.ApiKey, isPasswordClearText: true, validAuthenticationTypesText: null);
        }
        var providers = new List<Lazy<INuGetResourceProvider>>();
        providers.AddRange(global::NuGet.Protocol.Core.Types.Repository.Provider.GetCoreV3());
        return new SourceRepository(packageSource, providers);
    }
}
