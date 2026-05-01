using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NuGet.Packaging.Core;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using Namotion.NuGet.Plugins.Configuration;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Namotion.NuGet.Plugins.Repository;

/// <summary>
/// Downloads and searches NuGet packages from a single feed.
/// </summary>
public class NuGetPackageRepository : INuGetPackageRepository
{
    private readonly NuGetFeed _feed;
    private readonly SourceRepository _sourceRepository;
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
        string searchTerm, int take, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(searchTerm);
        var resource = await _sourceRepository.GetResourceAsync<PackageSearchResource>(cancellationToken);

        var results = await resource.SearchAsync(
            searchTerm,
            new SearchFilter(includePrerelease: _includePrerelease) { IncludeDelisted = false },
            0, take, global::NuGet.Common.NullLogger.Instance, cancellationToken);

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
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
        const int maxRetries = 5;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var metadataResource = await _sourceRepository.GetResourceAsync<PackageMetadataResource>(cancellationToken);

                var resolvedVersion = await ResolveVersionAsync(
                    packageName, packageVersion, metadataResource, cancellationToken);
                var identity = new PackageIdentity(packageName, resolvedVersion);

                using var metadataCacheContext = new SourceCacheContext();
                var metadata = await metadataResource.GetMetadataAsync(
                    identity, metadataCacheContext, global::NuGet.Common.NullLogger.Instance, cancellationToken);

                if (metadata == null)
                {
                    throw new PackageNotFoundException(packageName, packageVersion);
                }

                var downloadResource = await _sourceRepository.GetResourceAsync<DownloadResource>(cancellationToken);
                using var downloadCacheContext = new SourceCacheContext { DirectDownload = true };
                var downloadResult = await downloadResource.GetDownloadResourceResultAsync(
                    metadata.Identity,
                    new PackageDownloadContext(downloadCacheContext),
                    Path.GetTempPath(), global::NuGet.Common.NullLogger.Instance, cancellationToken);

                if (downloadResult.PackageStream == null)
                {
                    downloadResult.Dispose();
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
                (exception is HttpRequestException || exception is FatalProtocolException))
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                _logger.LogDebug(exception, "Download attempt {Attempt} failed for '{Package}'. Retrying in {Delay}s.",
                    attempt + 1, packageName, delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private async Task<NuGetVersion> ResolveVersionAsync(
        string packageName,
        string? packageVersion,
        PackageMetadataResource metadataResource,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(packageVersion))
        {
            if (!NuGetVersion.TryParse(packageVersion, out var parsedVersion))
            {
                throw new ArgumentException($"'{packageVersion}' is not a valid NuGet version for package '{packageName}'.", nameof(packageVersion));
            }

            return parsedVersion;
        }

        // Resolve latest version first to avoid NuGet SDK SingleOrDefault issue
        using var cacheContext = new SourceCacheContext();
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
