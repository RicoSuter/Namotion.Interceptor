using HomeBlaze.Abstractions;
using HomeBlaze.Plugins.Models;
using HomeBlaze.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using Xunit;

namespace HomeBlaze.Plugins.Tests;

/// <summary>
/// Exercises <see cref="NuGetPluginProvider"/> against the real sample plugin packages
/// (<c>MyCompany.SamplePlugin1.HomeBlaze</c> and <c>MyCompany.SamplePlugin2.HomeBlaze</c>) produced into
/// <c>src/HomeBlaze/HomeBlaze/Plugins</c> when the solution is built. Unlike <see cref="NuGetPluginProviderTests"/>,
/// which uses an empty feed and never loads anything, these tests cover the behavior that only shows up once a
/// plugin actually loads.
/// </summary>
[Trait("Category", "Integration")]
public class NuGetPluginProviderIntegrationTests : IDisposable
{
    private const string Plugin1 = "MyCompany.SamplePlugin1.HomeBlaze";
    private const string Plugin2 = "MyCompany.SamplePlugin2.HomeBlaze";

    // Guid-suffixed so nobody publishing a package with this exact name to nuget.org can flip the assertion.
    private static readonly string MissingPackageId = $"Missing.Package.{Guid.NewGuid():N}";

    private readonly DirectoryInfo _dataDirectory = Directory.CreateTempSubdirectory("homeblaze-plugins-integration-");

    [Fact]
    public async Task WhenProviderLoadsSamplePlugin_ThenItsTypesAreAddedAndTypesChangedIsRaised()
    {
        // Arrange
        var typeProvider = new TypeProvider();
        var raisedCount = 0;
        typeProvider.TypesChanged += (_, _) => raisedCount++;
        var provider = CreateProvider(typeProvider);
        provider.Plugins = [new PluginEntry { PackageName = Plugin1, Version = "1.0.0" }];

        // Act
        await provider.ReconcileAsync(CancellationToken.None);

        // Assert
        Assert.Equal(ServiceStatus.Running, provider.LoadedPlugins[Plugin1].Status);
        Assert.Contains(typeProvider.Types, type => type.FullName == "MyCompany.SamplePlugin1.SampleDevice1");
        Assert.True(raisedCount >= 1);
        Assert.True(Directory.Exists(Path.Combine(_dataDirectory.FullName, "Plugins", "Cache")));
    }

    [Fact]
    public async Task WhenPluginIsAddedAfterLoading_ThenItLoadsWithoutRestart()
    {
        // Arrange
        var typeProvider = new TypeProvider();
        var provider = await CreateProviderWithPlugin1LoadedAsync(typeProvider);

        // Act
        await provider.AddPluginAsync(Plugin2, "1.0.0", CancellationToken.None);

        // Assert
        Assert.Equal(ServiceStatus.Running, provider.LoadedPlugins[Plugin2].Status);
        Assert.Contains(typeProvider.Types, type => type.FullName == "MyCompany.SamplePlugin2.SampleDevice2");
        Assert.False(provider.IsRestartRequired);
    }

    [Fact]
    public async Task WhenLoadedPluginIsRemoved_ThenRestartIsRequired()
    {
        // Arrange
        var provider = await CreateProviderWithPlugin1LoadedAsync();

        // Act
        await provider.RemovePluginAsync(Plugin1);

        // Assert
        Assert.True(provider.IsRestartRequired);
        Assert.Contains("restart", provider.LoadedPlugins[Plugin1].StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenRemovedRunningPluginIsAddedBackWithSameVersion_ThenRestartIsNotRequired()
    {
        // Arrange
        var provider = await CreateProviderWithPlugin1LoadedAsync();
        await provider.RemovePluginAsync(Plugin1);

        // Act
        await provider.AddPluginAsync(Plugin1, "1.0.0", CancellationToken.None);

        // Assert
        Assert.False(provider.IsRestartRequired);
        var plugin = provider.LoadedPlugins[Plugin1];
        Assert.Equal(ServiceStatus.Running, plugin.Status);
        Assert.Null(plugin.StatusMessage);
    }

    [Fact]
    public async Task WhenRunningPluginVersionChanges_ThenRestartIsRequired()
    {
        // Arrange
        var provider = await CreateProviderWithPlugin1LoadedAsync();

        // Act
        provider.Plugins = [new PluginEntry { PackageName = Plugin1, Version = "0.0.1-doesnotexist" }];
        await provider.ReconcileAsync(CancellationToken.None);

        // Assert
        Assert.True(provider.IsRestartRequired);
        Assert.Equal("Version 0.0.1-doesnotexist takes effect after a restart.", provider.LoadedPlugins[Plugin1].StatusMessage);
    }

    [Fact]
    public async Task WhenRunningPluginVersionChangeIsReverted_ThenRestartIsNotRequired()
    {
        // Arrange
        var provider = await CreateProviderWithPlugin1LoadedAsync();
        provider.Plugins = [new PluginEntry { PackageName = Plugin1, Version = "0.0.1-doesnotexist" }];
        await provider.ReconcileAsync(CancellationToken.None);

        // Act
        provider.Plugins = [new PluginEntry { PackageName = Plugin1, Version = "1.0.0" }];
        await provider.ReconcileAsync(CancellationToken.None);

        // Assert
        Assert.False(provider.IsRestartRequired);
        Assert.Null(provider.LoadedPlugins[Plugin1].StatusMessage);
    }

    [Fact]
    public async Task WhenFeedsChangeAfterSuccessfulLoad_ThenRestartIsRequired()
    {
        // Arrange
        var provider = await CreateProviderWithPlugin1LoadedAsync();

        // Act
        provider.Feeds = [.. provider.Feeds, new PluginFeedEntry { Name = "other", Url = "https://example.test/feed" }];
        await provider.ReconcileAsync(CancellationToken.None);

        // Assert
        Assert.True(provider.IsRestartRequired);
    }

    [Fact]
    public async Task WhenRetryAsyncRunsAfterFeedIsPopulated_ThenItLoads()
    {
        // Arrange: an initially empty local feed, so the load fails for a reason other than a version mismatch.
        // Fixing it by changing the configured version would trigger a reload on its own (ReconcileAsync reloads
        // a failed entry whose version changed even without retryFailed), which would not actually exercise
        // RetryAsync's retryFailed path the way recovering from a feed outage does.
        var feedDirectory = Directory.CreateTempSubdirectory("homeblaze-plugins-retry-feed-");
        try
        {
            var provider = CreateProvider(new TypeProvider(), feedDirectory.FullName);
            provider.Plugins = [new PluginEntry { PackageName = Plugin1, Version = "1.0.0" }];
            await provider.ReconcileAsync(CancellationToken.None);
            Assert.Equal(ServiceStatus.Error, provider.LoadedPlugins[Plugin1].Status);

            // Act: populate the feed without touching the configuration, then retry with the same version.
            foreach (var packageName in new[] { Plugin1, "MyCompany.SamplePlugin1", "MyCompany.Abstractions" })
            {
                var fileName = $"{packageName}.1.0.0.nupkg";
                File.Copy(Path.Combine(FindPluginsFolder(), fileName), Path.Combine(feedDirectory.FullName, fileName));
            }

            await provider.RetryAsync(CancellationToken.None);

            // Assert
            Assert.Equal(ServiceStatus.Running, provider.LoadedPlugins[Plugin1].Status);
        }
        finally
        {
            feedDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task WhenFeedsChangeAfterEveryPluginFailed_ThenRetryLoadsFromNewFeedWithoutRestart()
    {
        // Arrange
        var emptyFeedDirectory = Directory.CreateTempSubdirectory("homeblaze-plugins-empty-feed-");
        try
        {
            var provider = CreateProvider(new TypeProvider(), emptyFeedDirectory.FullName);
            provider.Plugins = [new PluginEntry { PackageName = Plugin1, Version = "1.0.0" }];
            await provider.ReconcileAsync(CancellationToken.None);
            Assert.Equal(ServiceStatus.Error, provider.LoadedPlugins[Plugin1].Status);

            // Act
            provider.Feeds =
            [
                new PluginFeedEntry { Name = "samples", Url = FindPluginsFolder() },
                .. provider.Feeds.Where(feed => feed.Name != "samples")
            ];
            await provider.RetryAsync(CancellationToken.None);

            // Assert
            Assert.Equal(ServiceStatus.Running, provider.LoadedPlugins[Plugin1].Status);
            Assert.False(provider.IsRestartRequired);
        }
        finally
        {
            emptyFeedDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task WhenRetryingNextToMissingPackage_ThenLoadedPluginIsUntouched()
    {
        // Arrange
        var provider = CreateProvider(new TypeProvider());
        provider.Plugins =
        [
            new PluginEntry { PackageName = Plugin1, Version = "1.0.0" },
            new PluginEntry { PackageName = MissingPackageId, Version = "1.0.0" }
        ];
        await provider.ReconcileAsync(CancellationToken.None);
        var loadedPluginBeforeRetry = provider.LoadedPlugins[Plugin1];
        Assert.Equal(ServiceStatus.Error, provider.LoadedPlugins[MissingPackageId].Status);

        // Act
        await provider.RetryAsync(CancellationToken.None);

        // Assert
        Assert.Same(loadedPluginBeforeRetry, provider.LoadedPlugins[Plugin1]);
        Assert.Equal(ServiceStatus.Error, provider.LoadedPlugins[MissingPackageId].Status);
    }

    [Fact]
    public async Task WhenTypesChangedHandlerThrows_ThenPluginStaysRunningWithHandlerFailureReported()
    {
        // Arrange
        var typeProvider = new TypeProvider();
        typeProvider.TypesChanged += (_, _) => throw new InvalidOperationException("handler failure");
        var provider = CreateProvider(typeProvider);
        provider.Plugins = [new PluginEntry { PackageName = Plugin1, Version = "1.0.0" }];

        // Act
        await provider.ReconcileAsync(CancellationToken.None);

        // Assert
        var plugin = provider.LoadedPlugins[Plugin1];
        Assert.Equal(ServiceStatus.Running, plugin.Status);
        Assert.Contains(typeProvider.Types, type => type.FullName == "MyCompany.SamplePlugin1.SampleDevice1");
        Assert.Contains("handler failure", plugin.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<NuGetPluginProvider> CreateProviderWithPlugin1LoadedAsync(TypeProvider? typeProvider = null)
    {
        var provider = CreateProvider(typeProvider ?? new TypeProvider());
        provider.Plugins = [new PluginEntry { PackageName = Plugin1, Version = "1.0.0" }];
        await provider.ReconcileAsync(CancellationToken.None);
        return provider;
    }

    private NuGetPluginProvider CreateProvider(TypeProvider typeProvider, string? localFeedPath = null)
    {
        var provider = new NuGetPluginProvider(typeProvider, NullLoggerFactory.Instance)
        {
            // "samples" (or the caller's own local feed) serves the sample packages themselves; the sample
            // plugins also depend on ordinary NuGet packages (e.g. Bogus) that are not bundled locally, so
            // nuget.org resolves those.
            Feeds =
            [
                new PluginFeedEntry { Name = "samples", Url = localFeedPath ?? FindPluginsFolder() },
                new PluginFeedEntry { Name = "nuget.org", Url = "https://api.nuget.org/v3/index.json" }
            ],
            HostIdentifier = "HomeBlaze"
        };

        var context = InterceptorSubjectContext.Create();
        context.AddService<IDataDirectoryProvider>(new TestDataDirectoryProvider(_dataDirectory.FullName));
        ((IInterceptorSubject)provider).Context.AddFallbackContext(context);
        return provider;
    }

    private static string FindPluginsFolder()
    {
        var directory = Path.GetDirectoryName(typeof(NuGetPluginProviderIntegrationTests).Assembly.Location);
        while (directory != null)
        {
            var pluginsPath = Path.Combine(directory, "HomeBlaze", "Plugins");
            if (File.Exists(Path.Combine(pluginsPath, "MyCompany.SamplePlugin1.HomeBlaze.1.0.0.nupkg")))
            {
                return pluginsPath;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new InvalidOperationException("Could not find the sample plugin packages. Build the solution first.");
    }

    public void Dispose()
    {
        try
        {
            _dataDirectory.Delete(recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort: a loaded plugin assembly can still be locking a file in here (notably on Windows).
        }
    }

    private sealed class TestDataDirectoryProvider(string dataDirectory) : IDataDirectoryProvider
    {
        public string DataDirectory => dataDirectory;
    }
}
