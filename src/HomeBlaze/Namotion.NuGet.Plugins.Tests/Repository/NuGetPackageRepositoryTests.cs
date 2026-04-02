using Xunit;

using Namotion.NuGet.Plugins.Configuration;
using Namotion.NuGet.Plugins.Loading;
using Namotion.NuGet.Plugins.Repository;
using Namotion.NuGet.Plugins.Resolution;

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
        var results = await repository.SearchPackagesAsync("Newtonsoft.Json", 0, 10, CancellationToken.None);

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
        var (package, stream) = await repository.DownloadPackageAsync(
            "Newtonsoft.Json", "13.0.3", CancellationToken.None);
        using (stream)
        {
            // Assert
            Assert.Equal("Newtonsoft.Json", package.PackageName);
            Assert.Equal("13.0.3", package.PackageVersion);
            Assert.True(stream.Length > 0);
        }
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
        var (package, stream) = await repository.DownloadPackageAsync(
            "Newtonsoft.Json", null, CancellationToken.None);
        using (stream)
        {
            // Assert
            Assert.Equal("Newtonsoft.Json", package.PackageName);
            Assert.NotNull(package.PackageVersion);
        }
    }
}
