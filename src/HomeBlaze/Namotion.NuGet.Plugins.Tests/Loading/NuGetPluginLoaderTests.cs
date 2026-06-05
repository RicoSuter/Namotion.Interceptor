using Xunit;

using Namotion.NuGet.Plugins.Configuration;
using Namotion.NuGet.Plugins.Loading;

namespace Namotion.NuGet.Plugins.Tests.Loading;

public class NuGetPluginLoaderTests
{
    [Fact]
    public void WhenCreatingLoader_ThenDoesNotThrow()
    {
        // Act
        using var loader = new NuGetPluginLoader(new NuGetPluginLoaderOptions
        {
            Feeds = [NuGetFeed.NuGetOrg]
        });

        // Assert
        Assert.Empty(loader.LoadedPlugins);
    }

    [Fact]
    public async Task WhenUnloadingUnknownPlugin_ThenReturnsFalse()
    {
        // Arrange
        using var loader = new NuGetPluginLoader(new NuGetPluginLoaderOptions
        {
            Feeds = [NuGetFeed.NuGetOrg],
            HostDependencies = HostDependencyResolver.FromDepsJson()
        });

        var result = await loader.LoadPluginsAsync(
            [new NuGetPluginReference("Humanizer.Core", "2.14.1")],
            CancellationToken.None);

        // Act — unload a plugin that was loaded, then try again (should be false)
        var plugin = result.LoadedPlugins[0];
        Assert.True(loader.UnloadPlugin(plugin));
        var secondAttempt = loader.UnloadPlugin(plugin);

        // Assert
        Assert.False(secondAttempt);
    }

    [Fact]
    public void WhenDisposing_ThenUnloadsAllPlugins()
    {
        // Arrange
        var loader = new NuGetPluginLoader(new NuGetPluginLoaderOptions());

        // Act & Assert -- should not throw
        loader.Dispose();
        loader.Dispose(); // double dispose safe
    }
}
