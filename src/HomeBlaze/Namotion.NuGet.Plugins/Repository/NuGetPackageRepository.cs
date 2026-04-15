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
    private readonly global::NuGet.Protocol.Core.Types.SourceRepository _sourceRepository;
    private readonly bool _includePrerelease;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="NuGetPackageRepository"/> class.
    /// </summary>
    /// <param name="feed">The NuGet feed to use for package operations.</param>
    /// <param name="includePrerelease">Whether to include pre-release package versions.</param>
    /// <param name="logger">An optional logger for diagnostic output.</param>
    public NuGetPackageRepository(NuGetFeed feed, bool includePrerelease = false, ILogger? logger = null)
    {
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
        _sourceRepository = feed.CreateSourceRepository();
        _includePrerelease = includePrerelease;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<NuGetPackage>> SearchPackagesAsync(
        string searchTerm, int skip, int take, CancellationToken cancellationToken)
    {
        var sourceRepository = _sourceRepository;
        var resource = await sourceRepository.GetResourceAsync<global::NuGet.Protocol.Core.Types.PackageSearchResource>(cancellationToken);

        var results = await resource.SearchAsync(
            searchTerm,
            new global::NuGet.Protocol.Core.Types.SearchFilter(includePrerelease: _includePrerelease) { IncludeDelisted = false },
            skip, take, global::NuGet.Common.NullLogger.Instance, cancellationToken);

        return results.Select(metadata => new NuGetPackage(
            metadata.Identity.Id,
            metadata.Identity.Version.ToNormalizedString(),
            metadata.Title,
            metadata.Description,
            metadata.Authors));
    }

    /// <inheritdoc />
    public async Task<NuGetPackageDownload> DownloadPackageAsync(
        string packageName, string? packageVersion, CancellationToken cancellationToken)
    {
        const int maxRetries = 5;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var sourceRepository = _sourceRepository;

                var metadataResource = await sourceRepository.GetResourceAsync<global::NuGet.Protocol.Core.Types.PackageMetadataResource>(cancellationToken);

                var resolvedVersion = await ResolveVersionAsync(
                    packageName, packageVersion, metadataResource, cancellationToken);
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

                var packageInfo = new NuGetPackage(
                    metadata.Identity.Id,
                    metadata.Identity.Version.ToNormalizedString(),
                    metadata.Title,
                    metadata.Description,
                    metadata.Authors);

                return new NuGetPackageDownload(packageInfo, downloadResult.PackageStream, downloadResult);
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

    private async Task<global::NuGet.Versioning.NuGetVersion> ResolveVersionAsync(
        string packageName,
        string? packageVersion,
        global::NuGet.Protocol.Core.Types.PackageMetadataResource metadataResource,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(packageVersion))
        {
            return new global::NuGet.Versioning.NuGetVersion(packageVersion);
        }

        // Resolve latest version first to avoid NuGet SDK SingleOrDefault issue
        using var cacheContext = new global::NuGet.Protocol.Core.Types.SourceCacheContext();
        var versions = await metadataResource.GetMetadataAsync(
            packageName, includePrerelease: _includePrerelease, includeUnlisted: false,
            cacheContext, global::NuGet.Common.NullLogger.Instance, cancellationToken);

        var latest = versions?
            .OrderByDescending(m => m.Identity.Version)
            .FirstOrDefault();

        if (latest == null)
        {
            throw new PackageNotFoundException(packageName, packageVersion);
        }

        return latest.Identity.Version;
    }
}
