namespace Namotion.NuGet.Plugins.Repository;

/// <summary>
/// Abstraction for downloading and searching NuGet packages from a feed.
/// </summary>
public interface INuGetPackageRepository
{
    /// <summary>
    /// Searches for packages matching the given search term.
    /// </summary>
    Task<IEnumerable<NuGetPackage>> SearchPackagesAsync(
        string searchTerm, int skip, int take, CancellationToken cancellationToken);

    /// <summary>
    /// Downloads a package by name and optional version. Returns package metadata and the package stream.
    /// </summary>
    Task<NuGetPackageDownload> DownloadPackageAsync(
        string packageName, string? packageVersion, CancellationToken cancellationToken);
}
