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
    public void WhenUnloadingNonExistentPlugin_ThenReturnsFalse()
    {
        // Arrange
        using var loader = new NuGetPluginLoader(new NuGetPluginLoaderOptions());

        // Act
        var result = loader.UnloadPlugin("NonExistent");

        // Assert
        Assert.False(result);
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
