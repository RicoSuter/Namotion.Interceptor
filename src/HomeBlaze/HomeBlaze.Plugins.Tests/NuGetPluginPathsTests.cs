using Xunit;

namespace HomeBlaze.Plugins.Tests;

public class NuGetPluginPathsTests
{
    private static readonly string DataDirectory = Path.Combine(Path.GetTempPath(), "homeblaze-data");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WhenCacheDirectoryIsNotSet_ThenDefaultUnderDataDirectoryIsUsed(string? configured)
    {
        // Act
        var path = NuGetPluginPaths.ResolveCacheDirectory(configured, DataDirectory);

        // Assert
        Assert.Equal(Path.Combine(DataDirectory, "Plugins", "Cache"), path);
    }

    [Fact]
    public void WhenCacheDirectoryIsRelative_ThenItResolvesAgainstDataDirectory()
    {
        // Act
        var path = NuGetPluginPaths.ResolveCacheDirectory("MyCache", DataDirectory);

        // Assert
        Assert.Equal(Path.Combine(DataDirectory, "MyCache"), path);
    }

    [Fact]
    public void WhenCacheDirectoryIsAbsolute_ThenItIsUsedAsIs()
    {
        // Arrange
        var absolute = Path.Combine(Path.GetTempPath(), "shared-cache");

        // Act
        var path = NuGetPluginPaths.ResolveCacheDirectory(absolute, DataDirectory);

        // Assert
        Assert.Equal(absolute, path);
    }

    [Fact]
    public void WhenDataDirectoryIsMissing_ThenWorkingDirectoryIsUsed()
    {
        // Act
        var path = NuGetPluginPaths.ResolveCacheDirectory(null, dataDirectory: null);

        // Assert
        Assert.Equal(Path.GetFullPath(Path.Combine("Plugins", "Cache")), path);
    }

    [Theory]
    [InlineData("https://api.nuget.org/v3/index.json")]
    [InlineData("http://localhost:5555/v3/index.json")]
    public void WhenFeedUrlIsAbsoluteUri_ThenItIsUsedAsIs(string url)
    {
        // Act
        var resolved = NuGetPluginPaths.ResolveFeedUrl(url, DataDirectory);

        // Assert
        Assert.Equal(url, resolved);
    }

    [Fact]
    public void WhenFeedUrlIsRelativeFolder_ThenItResolvesAgainstDataDirectory()
    {
        // Act
        var resolved = NuGetPluginPaths.ResolveFeedUrl("../Plugins", DataDirectory);

        // Assert
        Assert.Equal(Path.GetFullPath(Path.Combine(DataDirectory, "../Plugins")), resolved);
    }

    [Fact]
    public void WhenFeedUrlIsRootedFolder_ThenItIsUsedAsIs()
    {
        // Arrange
        var folder = Path.Combine(Path.GetTempPath(), "packages");

        // Act
        var resolved = NuGetPluginPaths.ResolveFeedUrl(folder, DataDirectory);

        // Assert
        Assert.Equal(folder, resolved);
    }
}
