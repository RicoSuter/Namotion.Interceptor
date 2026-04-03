using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using Namotion.NuGet.Plugins.Configuration;

namespace Namotion.NuGet.Plugins.Repository;

/// <summary>
/// Downloads and searches NuGet packages from a single feed.
/// </summary>
public class NuGetPackageRepository : INuGetPackageRepository
{
    private readonly NuGetFeed _feed;
    private readonly ILogger _logger;

    public NuGetPackageRepository(NuGetFeed feed, ILogger? logger = null)
    {
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task<IEnumerable<NuGetPackageInfo>> SearchPackagesAsync(
        string searchTerm, int skip, int take, CancellationToken cancellationToken)
    {
        var sourceRepository = CreateSourceRepository();
        var resource = await sourceRepository.GetResourceAsync<global::NuGet.Protocol.Core.Types.PackageSearchResource>(cancellationToken);

        var results = await resource.SearchAsync(
            searchTerm,
            new global::NuGet.Protocol.Core.Types.SearchFilter(includePrerelease: false) { IncludeDelisted = false },
            skip, take, global::NuGet.Common.NullLogger.Instance, cancellationToken);

        return results.Select(metadata => new NuGetPackageInfo(
            metadata.Identity.Id,
            metadata.Identity.Version.ToNormalizedString(),
            metadata.Title,
            metadata.Description,
            metadata.Authors));
    }

    public async Task<(NuGetPackageInfo Package, Stream Stream)> DownloadPackageAsync(
        string packageName, string? packageVersion, CancellationToken cancellationToken)
    {
        const int maxRetries = 5;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var sourceRepository = CreateSourceRepository();

                var metadataResource = await sourceRepository.GetResourceAsync<global::NuGet.Protocol.Core.Types.PackageMetadataResource>(cancellationToken);

                global::NuGet.Versioning.NuGetVersion resolvedVersion;
                if (!string.IsNullOrEmpty(packageVersion))
                {
                    resolvedVersion = new global::NuGet.Versioning.NuGetVersion(packageVersion);
                }
                else
                {
                    // Resolve latest version first to avoid NuGet SDK SingleOrDefault issue
                    using var listCacheContext = new global::NuGet.Protocol.Core.Types.SourceCacheContext();
                    var versions = await metadataResource.GetMetadataAsync(
                        packageName, includePrerelease: false, includeUnlisted: false,
                        listCacheContext, global::NuGet.Common.NullLogger.Instance, cancellationToken);

                    var latest = versions?
                        .OrderByDescending(m => m.Identity.Version)
                        .FirstOrDefault();

                    if (latest == null)
                    {
                        throw new PackageNotFoundException(packageName, packageVersion);
                    }

                    resolvedVersion = latest.Identity.Version;
                }

                var identity = new global::NuGet.Packaging.Core.PackageIdentity(packageName, resolvedVersion);

                using var metadataCacheContext = new global::NuGet.Protocol.Core.Types.SourceCacheContext();
                var metadata = await metadataResource.GetMetadataAsync(
                    identity, metadataCacheContext, global::NuGet.Common.NullLogger.Instance, cancellationToken);

                if (metadata == null)
                {
                    throw new PackageNotFoundException(packageName, packageVersion);
                }

                var downloadResource = await sourceRepository.GetResourceAsync<global::NuGet.Protocol.Core.Types.DownloadResource>(cancellationToken);
                using var downloadCacheContext = new global::NuGet.Protocol.Core.Types.SourceCacheContext { DirectDownload = true };
                var downloadResult = await downloadResource.GetDownloadResourceResultAsync(
                    metadata.Identity,
                    new global::NuGet.Protocol.Core.Types.PackageDownloadContext(downloadCacheContext),
                    Path.GetTempPath(), global::NuGet.Common.NullLogger.Instance, cancellationToken);

                if (downloadResult.PackageStream == null)
                {
                    throw new HttpRequestException($"Package stream is empty for '{packageName}'. Retry.");
                }

                var packageInfo = new NuGetPackageInfo(
                    metadata.Identity.Id,
                    metadata.Identity.Version.ToNormalizedString(),
                    metadata.Title,
                    metadata.Description,
                    metadata.Authors);

                return (packageInfo, downloadResult.PackageStream);
            }
            catch (Exception exception) when (
                attempt < maxRetries &&
                (exception is HttpRequestException || exception is global::NuGet.Protocol.Core.Types.FatalProtocolException))
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                _logger.LogDebug(exception, "Download attempt {Attempt} failed for '{Package}'. Retrying in {Delay}s.",
                    attempt + 1, packageName, delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private global::NuGet.Protocol.Core.Types.SourceRepository CreateSourceRepository()
    {
        var packageSource = new global::NuGet.Configuration.PackageSource(_feed.Url);
        if (_feed.ApiKey != null)
        {
            packageSource.Credentials = new global::NuGet.Configuration.PackageSourceCredential(
                _feed.Url, "apikey", _feed.ApiKey, isPasswordClearText: true, validAuthenticationTypesText: null);
        }

        var providers = new List<Lazy<global::NuGet.Protocol.Core.Types.INuGetResourceProvider>>();
        providers.AddRange(global::NuGet.Protocol.Core.Types.Repository.Provider.GetCoreV3());
        return new global::NuGet.Protocol.Core.Types.SourceRepository(packageSource, providers);
    }
}
