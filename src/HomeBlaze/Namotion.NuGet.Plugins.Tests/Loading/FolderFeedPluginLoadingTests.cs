using Xunit;

using Namotion.NuGet.Plugins.Configuration;
using Namotion.NuGet.Plugins.Loading;

namespace Namotion.NuGet.Plugins.Tests.Loading;

[Trait("Category", "Integration")]
public class FolderFeedPluginLoadingTests : IDisposable
{
    private readonly string _cacheDir;

    public FolderFeedPluginLoadingTests()
    {
        _cacheDir = Path.Combine(Path.GetTempPath(), "NuGetFolderFeedTest_" + Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public async Task WhenLoadingFromFolderFeed_ThenResolvesTransitiveDependencies()
    {
        // Arrange
        var pluginsFolder = FindPluginsFolder();
        var options = CreateOptions(pluginsFolder);

        using var loader = new NuGetPluginLoader(options);

        // Act
        var result = await loader.LoadPluginsAsync(
            [new NuGetPluginRequest("HomeBlaze.SamplePlugin.HomeBlaze", "1.0.0")],
            CancellationToken.None);

        // Assert
        Assert.True(result.Success, string.Join(", ", result.Failures.Select(failure => failure.Reason)));
        Assert.Single(result.LoadedPlugins);

        var group = result.LoadedPlugins[0];
        Assert.True(group.Assemblies.Count >= 2, $"Expected at least 2 assemblies but got {group.Assemblies.Count}: [{string.Join(", ", group.Assemblies.Select(assembly => assembly.GetName().Name))}]");
        Assert.Contains(group.Assemblies, assembly => assembly.GetName().Name == "HomeBlaze.SamplePlugin");
        Assert.Contains(group.Assemblies, assembly => assembly.GetName().Name == "HomeBlaze.SamplePlugin.HomeBlaze");
        Assert.Contains(group.Assemblies, assembly => assembly.GetName().Name == "Bogus");
    }

    [Fact]
    public async Task WhenLoadingFromFolderFeed_ThenCanDiscoverSampleDeviceType()
    {
        // Arrange
        var pluginsFolder = FindPluginsFolder();
        var options = CreateOptions(pluginsFolder);

        using var loader = new NuGetPluginLoader(options);

        await loader.LoadPluginsAsync(
            [new NuGetPluginRequest("HomeBlaze.SamplePlugin.HomeBlaze", "1.0.0")],
            CancellationToken.None);

        // Act
        var sampleDeviceTypes = loader.GetTypes(type => type.Name == "SampleDevice").ToList();

        // Assert
        Assert.Single(sampleDeviceTypes);
        Assert.Equal("HomeBlaze.SamplePlugin", sampleDeviceTypes[0].Namespace);
    }

    public void Dispose()
    {
        if (Directory.Exists(_cacheDir))
        {
            try { Directory.Delete(_cacheDir, true); } catch { }
        }
    }

    private NuGetPluginLoaderOptions CreateOptions(string pluginsFolder)
    {
        // Build a HostDependencyResolver that only contains packages whose major version
        // matches the runtime, avoiding conflicts between the net9.0 test host dependencies
        // and net10.0 plugin transitive dependencies (e.g. Microsoft.Extensions.* 9.x vs 10.x).
        var runtimeMajor = Environment.Version.Major;
        var fullResolver = HostDependencyResolver.FromDepsJson();
        var filteredDependencies = fullResolver.Dependencies
            .Where(dependency => dependency.Value.Major == runtimeMajor)
            .Select(dependency => (dependency.Key, new Version(dependency.Value.Major, dependency.Value.Minor, dependency.Value.Patch)))
            .ToArray();
        var hostDependencies = HostDependencyResolver.FromAssemblies(filteredDependencies);

        return new NuGetPluginLoaderOptions
        {
            Feeds =
            [
                new NuGetFeed("local", pluginsFolder),
                NuGetFeed.NuGetOrg,
            ],
            CacheDirectory = _cacheDir,
            HostDependencies = hostDependencies,
            HostPackagePatterns = ["HomeBlaze.*.Abstractions"],
        };
    }

    private static string FindPluginsFolder()
    {
        var directory = Path.GetDirectoryName(typeof(FolderFeedPluginLoadingTests).Assembly.Location)!;
        while (directory != null)
        {
            var pluginsPath = Path.Combine(directory, "HomeBlaze", "Plugins");
            if (Directory.Exists(pluginsPath) &&
                File.Exists(Path.Combine(pluginsPath, "HomeBlaze.SamplePlugin.1.0.0.nupkg")))
            {
                return pluginsPath;
            }

            pluginsPath = Path.Combine(directory, "Plugins");
            if (Directory.Exists(pluginsPath) &&
                Path.GetFileName(directory) == "HomeBlaze" &&
                File.Exists(Path.Combine(pluginsPath, "HomeBlaze.SamplePlugin.1.0.0.nupkg")))
            {
                return pluginsPath;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new InvalidOperationException("Could not find Plugins folder. Build the solution first.");
    }
}
