namespace Namotion.NuGet.Plugins.Repository;

/// <summary>
/// Tries multiple repositories in order. First to have the package wins.
/// </summary>
public class CompositeNuGetPackageRepository : INuGetPackageRepository
{
    private readonly IReadOnlyList<INuGetPackageRepository> _repositories;

    /// <summary>
    /// Initializes a new instance of the <see cref="CompositeNuGetPackageRepository"/> class.
    /// </summary>
    /// <param name="repositories">The ordered list of repositories to try.</param>
    public CompositeNuGetPackageRepository(IReadOnlyList<INuGetPackageRepository> repositories)
    {
        _repositories = repositories ?? throw new ArgumentNullException(nameof(repositories));
    }

    /// <inheritdoc />
    public async Task<NuGetPackageDownload> DownloadPackageAsync(
        string packageName, string? packageVersion, CancellationToken cancellationToken)
    {
        foreach (var repository in _repositories)
        {
            try
            {
                return await repository.DownloadPackageAsync(packageName, packageVersion, cancellationToken);
            }
            catch (PackageNotFoundException)
            {
                // Try next repository
            }
        }

        throw new PackageNotFoundException(packageName, packageVersion);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<NuGetPackage>> SearchPackagesAsync(
        string searchTerm, int skip, int take, CancellationToken cancellationToken)
    {
        var allResults = new List<NuGetPackage>();
        foreach (var repository in _repositories)
        {
            var results = await repository.SearchPackagesAsync(searchTerm, skip, take, cancellationToken);
            allResults.AddRange(results);
        }

        return allResults
            .GroupBy(p => p.PackageName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(p =>
                global::NuGet.Versioning.NuGetVersion.TryParse(p.PackageVersion, out var version)
                    ? version
                    : new global::NuGet.Versioning.NuGetVersion(0, 0, 0)).First());
    }
}
