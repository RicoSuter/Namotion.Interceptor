using Xunit;

using Namotion.NuGet.Plugins.Configuration;
using Namotion.NuGet.Plugins.Loading;

namespace Namotion.NuGet.Plugins.Tests.Loading;

[Trait("Category", "Integration")]
public class NuGetPluginLoaderIntegrationTests : IDisposable
{
    private readonly string _cacheDir;

    public NuGetPluginLoaderIntegrationTests()
    {
        _cacheDir = Path.Combine(Path.GetTempPath(), "NuGetPluginsIntTest_" + Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public async Task WhenLoadingRealPackage_ThenAssembliesAreAvailable()
    {
        // Arrange
        using var loader = new NuGetPluginLoader(new NuGetPluginLoaderOptions
        {
            Feeds = [NuGetFeed.NuGetOrg],
            CacheDirectory = _cacheDir,
            HostDependencies = HostDependencyResolver.FromDepsJson()
        });

        // Act -- load a small, well-known package
        var result = await loader.LoadPluginsAsync(
            [new NuGetPluginReference("Humanizer.Core", "2.14.1")],
            CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Single(result.LoadedPlugins);
        Assert.True(result.LoadedPlugins[0].Assemblies.Count > 0);
    }

    [Fact]
    public async Task WhenLoadingPackageWithDependencies_ThenTransitiveDepsResolved()
    {
        // Arrange
        using var loader = new NuGetPluginLoader(new NuGetPluginLoaderOptions
        {
            Feeds = [NuGetFeed.NuGetOrg],
            CacheDirectory = _cacheDir,
            HostDependencies = HostDependencyResolver.FromDepsJson()
        });

        // Act -- Polly has dependencies
        var result = await loader.LoadPluginsAsync(
            [new NuGetPluginReference("Polly", "8.5.2")],
            CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.True(result.LoadedPlugins[0].Assemblies.Count >= 1);
    }

    [Fact]
    public async Task WhenUnloadingPlugin_ThenItIsRemoved()
    {
        // Arrange
        using var loader = new NuGetPluginLoader(new NuGetPluginLoaderOptions
        {
            Feeds = [NuGetFeed.NuGetOrg],
            CacheDirectory = _cacheDir,
            HostDependencies = HostDependencyResolver.FromDepsJson()
        });

        await loader.LoadPluginsAsync(
            [new NuGetPluginReference("Humanizer.Core", "2.14.1")],
            CancellationToken.None);

        // Act
        var plugin = loader.LoadedPlugins.First(p => p.PackageName == "Humanizer.Core");
        var unloaded = loader.UnloadPlugin(plugin);

        // Assert
        Assert.True(unloaded);
        Assert.Empty(loader.LoadedPlugins);
    }

    [Fact]
    public async Task WhenPackageNotFound_ThenReportsFailure()
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
            [new NuGetPluginReference("NonExistent.Package.XYZ.12345", "1.0.0")],
            CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Single(result.Failures);
    }

    [Fact]
    public async Task WhenGetTypes_ThenFindsTypesAcrossPlugins()
    {
        // Arrange
        using var loader = new NuGetPluginLoader(new NuGetPluginLoaderOptions
        {
            Feeds = [NuGetFeed.NuGetOrg],
            CacheDirectory = _cacheDir,
            HostDependencies = HostDependencyResolver.FromDepsJson()
        });

        await loader.LoadPluginsAsync(
            [new NuGetPluginReference("Humanizer.Core", "2.14.1")],
            CancellationToken.None);

        // Act
        var types = loader.GetTypes(t => t.IsPublic).ToList();

        // Assert
        Assert.NotEmpty(types);
    }

    public void Dispose()
    {
        if (Directory.Exists(_cacheDir))
        {
            try { Directory.Delete(_cacheDir, true); } catch { }
        }
    }
}
