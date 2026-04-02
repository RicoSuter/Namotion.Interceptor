namespace Namotion.NuGet.Plugins.Repository;

/// <summary>
/// Tries multiple repositories in order. First to have the package wins.
/// </summary>
public class CompositeNuGetPackageRepository : INuGetPackageRepository
{
    private readonly IReadOnlyList<INuGetPackageRepository> _repositories;

    public CompositeNuGetPackageRepository(IReadOnlyList<INuGetPackageRepository> repositories)
    {
        _repositories = repositories ?? throw new ArgumentNullException(nameof(repositories));
    }

    public async Task<(NuGetPackageInfo Package, Stream Stream)> DownloadPackageAsync(
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

    public async Task<IEnumerable<NuGetPackageInfo>> SearchPackagesAsync(
        string searchTerm, int skip, int take, CancellationToken cancellationToken)
    {
        var allResults = new List<NuGetPackageInfo>();
        foreach (var repository in _repositories)
        {
            var results = await repository.SearchPackagesAsync(searchTerm, skip, take, cancellationToken);
            allResults.AddRange(results);
        }
        return allResults;
    }
}
