using Xunit;

using Namotion.NuGet.Plugins.Configuration;
using Namotion.NuGet.Plugins.Loading;
using Namotion.NuGet.Plugins.Repository;
using Namotion.NuGet.Plugins.Resolution;

namespace Namotion.NuGet.Plugins.Tests.Loading;

public class NuGetPluginLoaderEdgeCaseTests : IDisposable
{
    private readonly string _cacheDir;

    public NuGetPluginLoaderEdgeCaseTests()
    {
        _cacheDir = Path.Combine(Path.GetTempPath(), "NuGetEdge_" + Guid.NewGuid().ToString("N"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task WhenOnePluginFailsAndAnotherSucceeds_ThenGoodPluginStillLoads()
    {
        // Arrange
        using var loader = new NuGetPluginLoader(new NuGetPluginLoaderOptions
        {
            Feeds = [NuGetFeed.NuGetOrg],
            CacheDirectory = _cacheDir,
            HostDependencies = HostDependencyResolver.FromDepsJson()
        });

        // Act
        var result = await loader.LoadPluginsAsync(
        [
            new NuGetPluginReference("Humanizer.Core", "2.14.1"),
            new NuGetPluginReference("NonExistent.Package.XYZ.99999", "1.0.0")
        ], CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Single(result.LoadedPlugins);
        Assert.Equal("Humanizer.Core", result.LoadedPlugins[0].PackageName);
        Assert.Single(result.Failures);
        Assert.Equal("NonExistent.Package.XYZ.99999", result.Failures[0].PackageName);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task WhenLoadingMultiplePlugins_ThenGetTypesReturnsFromAll()
    {
        // Arrange
        using var loader = new NuGetPluginLoader(new NuGetPluginLoaderOptions
        {
            Feeds = [NuGetFeed.NuGetOrg],
            CacheDirectory = _cacheDir,
            HostDependencies = HostDependencyResolver.FromDepsJson()
        });

        // Act
        var result = await loader.LoadPluginsAsync(
        [
            new NuGetPluginReference("Humanizer.Core", "2.14.1"),
        ], CancellationToken.None);

        var allTypes = loader.GetTypes(t => t.IsPublic).ToList();
        var pluginTypes = result.LoadedPlugins[0].GetTypes(t => t.IsPublic).ToList();

        // Assert
        Assert.NotEmpty(allTypes);
        Assert.Equal(pluginTypes.Count, allTypes.Count);
    }

    [Fact]
    public async Task WhenLoadingEmptyPluginList_ThenSucceedsWithNoPlugins()
    {
        // Arrange
        using var loader = new NuGetPluginLoader(new NuGetPluginLoaderOptions
        {
            CacheDirectory = _cacheDir
        });

        // Act
        var result = await loader.LoadPluginsAsync([], CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Empty(result.LoadedPlugins);
        Assert.Empty(result.Failures);
    }

    public void Dispose()
    {
        if (Directory.Exists(_cacheDir))
            try { Directory.Delete(_cacheDir, true); } catch { }
    }
}
