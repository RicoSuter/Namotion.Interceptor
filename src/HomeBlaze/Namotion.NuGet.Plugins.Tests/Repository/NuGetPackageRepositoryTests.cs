using Xunit;

using Namotion.NuGet.Plugins.Configuration;
using Namotion.NuGet.Plugins.Repository;

namespace Namotion.NuGet.Plugins.Tests.Repository;

[Trait("Category", "Integration")]
public class NuGetPackageRepositoryTests
{
    [Fact]
    public async Task WhenSearchingPackages_ThenReturnsResults()
    {
        // Arrange
        var repository = new NuGetPackageRepository(NuGetFeed.NuGetOrg);

        // Act
        var results = await repository.SearchPackagesAsync("Newtonsoft.Json", 10, CancellationToken.None);

        // Assert
        Assert.NotEmpty(results);
        Assert.Contains(results, p => p.PackageName == "Newtonsoft.Json");
    }

    [Fact]
    public async Task WhenDownloadingExistingPackage_ThenReturnsStream()
    {
        // Arrange
        var repository = new NuGetPackageRepository(NuGetFeed.NuGetOrg);

        // Act
        await using var download = await repository.DownloadPackageAsync(
            "Newtonsoft.Json", "13.0.3", CancellationToken.None);

        // Assert
        Assert.Equal("Newtonsoft.Json", download.Package.PackageName);
        Assert.Equal("13.0.3", download.Package.PackageVersion);
        Assert.True(download.Stream.Length > 0);
    }

    [Fact]
    public async Task WhenDownloadingNonExistentPackage_ThenThrowsPackageNotFoundException()
    {
        // Arrange
        var repository = new NuGetPackageRepository(NuGetFeed.NuGetOrg);

        // Act & Assert
        await Assert.ThrowsAsync<PackageNotFoundException>(async () =>
            await repository.DownloadPackageAsync(
                Guid.NewGuid().ToString(), "1.0.0", CancellationToken.None));
    }

    [Fact]
    public async Task WhenDownloadingWithNullVersion_ThenReturnsLatest()
    {
        // Arrange
        var repository = new NuGetPackageRepository(NuGetFeed.NuGetOrg);

        // Act
        await using var download = await repository.DownloadPackageAsync(
            "Newtonsoft.Json", null, CancellationToken.None);

        // Assert
        Assert.Equal("Newtonsoft.Json", download.Package.PackageName);
        Assert.NotNull(download.Package.PackageVersion);
    }
}
