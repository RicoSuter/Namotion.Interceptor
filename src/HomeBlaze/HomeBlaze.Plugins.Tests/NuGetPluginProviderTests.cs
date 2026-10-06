using HomeBlaze.Abstractions;
using HomeBlaze.Plugins.Models;
using HomeBlaze.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Testing;
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
        await provider.ReconcileAsync(CancellationToken.None);

        // Assert
        var plugin = provider.LoadedPlugins["Missing.Package"];
        Assert.NotSame(failedPlugin, plugin);
        Assert.Equal(ServiceStatus.Error, plugin.Status);
        Assert.False(provider.IsRestartRequired);
    }

    [Fact]
    public async Task WhenFeedsChangeBeforeAnyLoad_ThenRestartIsNotRequired()
    {
        // Arrange
        var provider = CreateProvider();
        await provider.ReconcileAsync(CancellationToken.None);

        // Act
        provider.Feeds = [.. provider.Feeds, new PluginFeedEntry { Name = "other", Url = "Other" }];
        await provider.ReconcileAsync(CancellationToken.None);

        // Assert
        Assert.False(provider.IsRestartRequired);
    }

    [Fact]
    public async Task WhenLoadedPluginIsRemoved_ThenRestartIsRequired()
    {
        // Arrange
        var provider = CreateProviderWithLoadedPlugin("Loaded.Package", "1.0.0", "Skipped 1 types");

        // Act
        await provider.RemovePluginAsync("Loaded.Package");

        // Assert
        Assert.True(provider.IsRestartRequired);
        Assert.Equal("Removed. Takes effect after a restart.", provider.LoadedPlugins["Loaded.Package"].StatusMessage);
        Assert.Equal("Warning", provider.IconColor);
    }

    [Fact]
    public async Task WhenRemovedPluginIsAddedAgain_ThenRestartIsNotRequired()
    {
        // Arrange
        var provider = CreateProviderWithLoadedPlugin("Loaded.Package", "1.0.0", "Skipped 1 types");
        await provider.RemovePluginAsync("Loaded.Package");

        // Act
        await provider.AddPluginAsync("Loaded.Package", "1.0.0", CancellationToken.None);

        // Assert
        Assert.False(provider.IsRestartRequired);
        var plugin = provider.LoadedPlugins["Loaded.Package"];
        Assert.Equal(ServiceStatus.Running, plugin.Status);
        Assert.Equal("Skipped 1 types", plugin.StatusMessage);
    }

    [Fact]
    public async Task WhenLoadedPluginVersionChanges_ThenRestartIsRequired()
    {
        // Arrange
        var provider = CreateProviderWithLoadedPlugin("Loaded.Package", "1.0.0", null);

        // Act
        provider.Plugins = [new PluginEntry { PackageName = "Loaded.Package", Version = "2.0.0" }];
        await provider.ReconcileAsync(CancellationToken.None);

        // Assert
        Assert.True(provider.IsRestartRequired);
        Assert.Equal("Version 2.0.0 takes effect after a restart.", provider.LoadedPlugins["Loaded.Package"].StatusMessage);
    }

    [Fact]
    public async Task WhenVersionChangeIsReverted_ThenRestartIsNotRequired()
    {
        // Arrange
        var provider = CreateProviderWithLoadedPlugin("Loaded.Package", "1.0.0", null);
        provider.Plugins = [new PluginEntry { PackageName = "Loaded.Package", Version = "2.0.0" }];
        await provider.ReconcileAsync(CancellationToken.None);

        // Act
        provider.Plugins = [new PluginEntry { PackageName = "Loaded.Package", Version = "1.0" }];
        await provider.ReconcileAsync(CancellationToken.None);

        // Assert
        Assert.False(provider.IsRestartRequired);
        Assert.Null(provider.LoadedPlugins["Loaded.Package"].StatusMessage);
    }

    [Fact]
    public async Task WhenConfigurationIsApplied_ThenPluginsAreLoadedInBackground()
    {
        // Arrange
        var provider = CreateProvider();
        provider.Plugins = [new PluginEntry { PackageName = "Missing.Package", Version = "1.0.0" }];

        // Act
        await provider.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => provider.LoadedPlugins.ContainsKey("Missing.Package"));
        Assert.Equal(ServiceStatus.Error, provider.LoadedPlugins["Missing.Package"].Status);
    }

    [Fact]
    public async Task WhenDifferentPluginsAreAddedConcurrently_ThenBothAreConfigured()
    {
        // Arrange
        var provider = CreateProvider();

        // Act
        await Task.WhenAll(
            Task.Run(() => provider.AddPluginAsync("First.Package", "1.0.0", CancellationToken.None)),
            Task.Run(() => provider.AddPluginAsync("Second.Package", "1.0.0", CancellationToken.None)));

        // Assert
        Assert.Equal(["First.Package", "Second.Package"], provider.Plugins.Select(plugin => plugin.PackageName).Order());
        Assert.Equal(2, provider.LoadedPlugins.Count);
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

    private NuGetPluginProvider CreateProviderWithLoadedPlugin(string packageName, string version, string? loadStatusMessage)
    {
        var provider = CreateProvider();
        provider.Plugins = [new PluginEntry { PackageName = packageName, Version = version }];
        provider.LoadedPlugins = new Dictionary<string, Plugin>(StringComparer.OrdinalIgnoreCase)
        {
            [packageName] = new Plugin(provider)
            {
                Name = packageName,
                Version = version,
                Status = ServiceStatus.Running,
                StatusMessage = loadStatusMessage
            }
        };
        provider.RequestedVersions[packageName] = version;
        provider.LoadStatusMessages[packageName] = loadStatusMessage;
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
