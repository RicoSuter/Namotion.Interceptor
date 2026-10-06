using HomeBlaze.Abstractions;
using HomeBlaze.Plugins.Models;
using HomeBlaze.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using Namotion.NuGet.Plugins.Configuration;
using Xunit;

namespace HomeBlaze.Plugins.Tests;

public class NuGetPluginProviderTests : IDisposable
{
    private readonly DirectoryInfo _dataDirectory = Directory.CreateTempSubdirectory("homeblaze-plugins-");

    [Fact]
    public async Task WhenPackageIsMissingFromFeed_ThenPluginShowsError()
    {
        // Arrange
        var provider = CreateProvider();
        provider.Plugins = [new PluginEntry { PackageName = "Missing.Package", Version = "1.0.0" }];

        // Act
        await provider.ReconcileAsync(CancellationToken.None);

        // Assert
        var plugin = provider.LoadedPlugins["Missing.Package"];
        Assert.Equal(ServiceStatus.Error, plugin.Status);
        Assert.False(string.IsNullOrEmpty(plugin.StatusMessage));
        Assert.Equal("Warning", provider.IconColor);
    }

    [Fact]
    public async Task WhenFailedPluginIsRemoved_ThenItDisappearsWithoutRestart()
    {
        // Arrange
        var provider = CreateProvider();
        provider.Plugins = [new PluginEntry { PackageName = "Missing.Package", Version = "1.0.0" }];
        await provider.ReconcileAsync(CancellationToken.None);

        // Act
        await provider.RemovePluginAsync("Missing.Package");

        // Assert
        Assert.Empty(provider.LoadedPlugins);
        Assert.False(provider.IsRestartRequired);
    }

    [Fact]
    public async Task WhenFailedPluginVersionChanges_ThenItIsLoadedAgainWithoutRestart()
    {
        // Arrange
        var provider = CreateProvider();
        provider.Plugins = [new PluginEntry { PackageName = "Missing.Package", Version = "1.0.0" }];
        await provider.ReconcileAsync(CancellationToken.None);
        var failedPlugin = provider.LoadedPlugins["Missing.Package"];

        // Act
        provider.Plugins = [new PluginEntry { PackageName = "Missing.Package", Version = "2.0.0" }];
        await provider.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        var plugin = provider.LoadedPlugins["Missing.Package"];
        Assert.NotSame(failedPlugin, plugin);
        Assert.Equal(ServiceStatus.Error, plugin.Status);
        Assert.False(provider.IsRestartRequired);
    }

    [Fact]
    public async Task WhenFeedsChangeAfterLoading_ThenRestartIsRequired()
    {
        // Arrange
        var provider = CreateProvider();
        await provider.ReconcileAsync(CancellationToken.None);

        // Act
        provider.Feeds = [.. provider.Feeds, new PluginFeedEntry { Name = "other", Url = "Other" }];
        await provider.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        Assert.True(provider.IsRestartRequired);
    }

    [Fact]
    public async Task WhenPluginIsAddedTwice_ThenSecondAddThrows()
    {
        // Arrange
        var provider = CreateProvider();
        await provider.AddPluginAsync("Missing.Package", "1.0.0", CancellationToken.None);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.AddPluginAsync("missing.package", "1.0.0", CancellationToken.None));
    }

    [Fact]
    public async Task WhenFeedHasEmptyUrl_ThenItIsSkipped()
    {
        // Arrange
        var provider = CreateProvider();
        provider.Feeds = [new PluginFeedEntry { Name = "empty", Url = " " }, .. provider.Feeds];
        provider.Plugins = [new PluginEntry { PackageName = "Missing.Package", Version = "1.0.0" }];

        // Act
        var options = provider.CreateLoaderOptions(_dataDirectory.FullName);
        await provider.ReconcileAsync(CancellationToken.None);

        // Assert
        var feed = Assert.Single(options.Feeds);
        Assert.Equal("local", feed.Name);
        Assert.Equal(ServiceStatus.Error, provider.LoadedPlugins["Missing.Package"].Status);
    }

    [Fact]
    public void WhenNoFeedHasUrl_ThenNuGetOrgIsUsed()
    {
        // Arrange
        var provider = CreateProvider();
        provider.Feeds = [new PluginFeedEntry { Name = "empty", Url = "" }];

        // Act
        var options = provider.CreateLoaderOptions(_dataDirectory.FullName);

        // Assert
        Assert.Same(NuGetFeed.NuGetOrg, Assert.Single(options.Feeds));
    }

    private NuGetPluginProvider CreateProvider()
    {
        Directory.CreateDirectory(Path.Combine(_dataDirectory.FullName, "Feed"));
        var provider = new NuGetPluginProvider(new TypeProvider(), NullLoggerFactory.Instance)
        {
            Feeds = [new PluginFeedEntry { Name = "local", Url = "Feed" }]
        };

        var context = InterceptorSubjectContext.Create();
        context.AddService<IDataDirectoryProvider>(new TestDataDirectoryProvider(_dataDirectory.FullName));
        ((IInterceptorSubject)provider).Context.AddFallbackContext(context);
        return provider;
    }

    public void Dispose()
    {
        _dataDirectory.Delete(recursive: true);
    }

    private sealed class TestDataDirectoryProvider(string dataDirectory) : IDataDirectoryProvider
    {
        public string DataDirectory => dataDirectory;
    }
}
