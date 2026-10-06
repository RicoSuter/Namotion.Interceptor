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
        var provider = CreateProvider(typeProvider);
        provider.Plugins = [new PluginEntry { PackageName = Plugin1, Version = "1.0.0" }];
        await provider.ReconcileAsync(CancellationToken.None);

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
        var provider = CreateProvider(new TypeProvider());
        provider.Plugins = [new PluginEntry { PackageName = Plugin1, Version = "1.0.0" }];
        await provider.ReconcileAsync(CancellationToken.None);

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
        var provider = CreateProvider(new TypeProvider());
        provider.Plugins = [new PluginEntry { PackageName = Plugin1, Version = "1.0.0" }];
        await provider.ReconcileAsync(CancellationToken.None);
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
        var provider = CreateProvider(new TypeProvider());
        provider.Plugins = [new PluginEntry { PackageName = Plugin1, Version = "1.0.0" }];
        await provider.ReconcileAsync(CancellationToken.None);

        // Act
        provider.Plugins = [new PluginEntry { PackageName = Plugin1, Version = "9.9.9" }];
        await provider.ReconcileAsync(CancellationToken.None);

        // Assert
        Assert.True(provider.IsRestartRequired);
        Assert.Equal("Version 9.9.9 takes effect after a restart.", provider.LoadedPlugins[Plugin1].StatusMessage);
    }

    [Fact]
    public async Task WhenRunningPluginVersionChangeIsReverted_ThenRestartIsNotRequired()
    {
        // Arrange
        var provider = CreateProvider(new TypeProvider());
        provider.Plugins = [new PluginEntry { PackageName = Plugin1, Version = "1.0.0" }];
        await provider.ReconcileAsync(CancellationToken.None);
        provider.Plugins = [new PluginEntry { PackageName = Plugin1, Version = "9.9.9" }];
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
        var provider = CreateProvider(new TypeProvider());
        provider.Plugins = [new PluginEntry { PackageName = Plugin1, Version = "1.0.0" }];
        await provider.ReconcileAsync(CancellationToken.None);

        // Act
        provider.Feeds = [.. provider.Feeds, new PluginFeedEntry { Name = "other", Url = "https://example.test/feed" }];
        await provider.ReconcileAsync(CancellationToken.None);

        // Assert
        Assert.True(provider.IsRestartRequired);
    }

    [Fact]
    public async Task WhenPluginVersionIsFixedAndRetried_ThenItLoads()
    {
        // Arrange
        var provider = CreateProvider(new TypeProvider());
        provider.Plugins = [new PluginEntry { PackageName = Plugin2, Version = "9.9.9" }];
        await provider.ReconcileAsync(CancellationToken.None);
        Assert.Equal(ServiceStatus.Error, provider.LoadedPlugins[Plugin2].Status);

        // Act
        provider.Plugins = [new PluginEntry { PackageName = Plugin2, Version = "1.0.0" }];
        await provider.RetryAsync(CancellationToken.None);

        // Assert
        Assert.Equal(ServiceStatus.Running, provider.LoadedPlugins[Plugin2].Status);
    }

    [Fact]
    public async Task WhenRetryingNextToMissingPackage_ThenLoadedPluginIsUntouched()
    {
        // Arrange
        var provider = CreateProvider(new TypeProvider());
        provider.Plugins =
        [
            new PluginEntry { PackageName = Plugin1, Version = "1.0.0" },
            new PluginEntry { PackageName = "Missing.Package", Version = "1.0.0" }
        ];
        await provider.ReconcileAsync(CancellationToken.None);
        var loadedPluginBeforeRetry = provider.LoadedPlugins[Plugin1];
        Assert.Equal(ServiceStatus.Error, provider.LoadedPlugins["Missing.Package"].Status);

        // Act
        await provider.RetryAsync(CancellationToken.None);

        // Assert
        Assert.Same(loadedPluginBeforeRetry, provider.LoadedPlugins[Plugin1]);
        Assert.Equal(ServiceStatus.Error, provider.LoadedPlugins["Missing.Package"].Status);
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

    private NuGetPluginProvider CreateProvider(TypeProvider typeProvider)
    {
        var provider = new NuGetPluginProvider(typeProvider, NullLoggerFactory.Instance)
        {
            // "samples" serves the sample packages themselves; the sample plugins also depend on ordinary
            // NuGet packages (e.g. Bogus) that are not bundled locally, so nuget.org resolves those.
            Feeds =
            [
                new PluginFeedEntry { Name = "samples", Url = FindPluginsFolder() },
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
        _dataDirectory.Delete(recursive: true);
    }

    private sealed class TestDataDirectoryProvider(string dataDirectory) : IDataDirectoryProvider
    {
        public string DataDirectory => dataDirectory;
    }
}
