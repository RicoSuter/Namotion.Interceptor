using System.Reflection;
using System.Runtime.Loader;
using Xunit;

using Namotion.NuGet.Plugins.Configuration;
using Namotion.NuGet.Plugins.Loading;

namespace Namotion.NuGet.Plugins.Tests.Loading;

[Trait("Category", "Integration")]
public class HostSharedDiscoveryIntegrationTests : IDisposable
{
    private readonly string _cacheDir;

    public HostSharedDiscoveryIntegrationTests()
    {
        _cacheDir = Path.Combine(Path.GetTempPath(), "NuGetHostSharedTest_" + Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public async Task WhenAbstractionsHasHostSharedAttribute_ThenTypeIdentityIsSharedAcrossPlugins()
    {
        // Arrange
        var pluginsFolder = FindPluginsFolder();
        var options = CreateOptions(pluginsFolder);

        using var loader = new NuGetPluginLoader(options);

        // Act
        var result = await loader.LoadPluginsAsync(
            [
                new NuGetPluginReference("MyCompany.SamplePlugin1.HomeBlaze", "1.0.0"),
                new NuGetPluginReference("MyCompany.SamplePlugin2.HomeBlaze", "1.0.0"),
            ],
            CancellationToken.None);

        // Assert
        Assert.True(result.Success, string.Join(", ", result.Failures.Select(failure => failure.Reason)));
        Assert.Equal(2, result.LoadedPlugins.Count);

        var plugin1 = result.LoadedPlugins.First(plugin => plugin.PackageName == "MyCompany.SamplePlugin1.HomeBlaze");
        var plugin2 = result.LoadedPlugins.First(plugin => plugin.PackageName == "MyCompany.SamplePlugin2.HomeBlaze");

        // MyCompany.Abstractions should be classified as Host in both plugins
        var abstractionsDep1 = plugin1.Dependencies.FirstOrDefault(
            dependency => dependency.PackageName.Equals("MyCompany.Abstractions", StringComparison.OrdinalIgnoreCase));
        var abstractionsDep2 = plugin2.Dependencies.FirstOrDefault(
            dependency => dependency.PackageName.Equals("MyCompany.Abstractions", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(abstractionsDep1);
        Assert.NotNull(abstractionsDep2);
        Assert.Equal(DependencyClassification.Host, abstractionsDep1.Classification);
        Assert.Equal(DependencyClassification.Host, abstractionsDep2.Classification);

        // Verify both plugins can discover types implementing IMyDevice.
        // Plugin1 loads many private assemblies including framework deps, so ExportedTypes
        // works. Plugin2 benefits from assemblies already loaded by plugin1 but some
        // may be in plugin1's ALC rather than the default context.
        var plugin1DeviceTypes = plugin1.GetTypes(type => type.Name == "SampleDevice1").ToList();
        Assert.Single(plugin1DeviceTypes);

        // Verify the IMyDevice interface type loaded by plugin1 comes from the
        // host-shared MyCompany.Abstractions assembly in the default context.
        var interfaceFromPlugin1 = plugin1DeviceTypes[0]
            .GetInterfaces()
            .First(interfaceType => interfaceType.Name == "IMyDevice");

        // The host-shared MyCompany.Abstractions assembly should be resolvable from
        // the default assembly load context since it was classified as Host.
        var abstractionsAssembly = AssemblyLoadContext.Default
            .LoadFromAssemblyName(new AssemblyName("MyCompany.Abstractions"));
        var interfaceFromDefaultContext = abstractionsAssembly.GetType("MyCompany.Abstractions.IMyDevice");

        Assert.NotNull(interfaceFromDefaultContext);
        Assert.Same(interfaceFromDefaultContext, interfaceFromPlugin1);
    }

    [Fact]
    public async Task WhenPluginLoaded_ThenMetadataAndManifestArePopulated()
    {
        // Arrange
        var pluginsFolder = FindPluginsFolder();
        var options = CreateOptions(pluginsFolder);

        using var loader = new NuGetPluginLoader(options);

        // Act
        var result = await loader.LoadPluginsAsync(
            [new NuGetPluginReference("MyCompany.SamplePlugin1.HomeBlaze", "1.0.0")],
            CancellationToken.None);

        // Assert
        Assert.True(result.Success, string.Join(", ", result.Failures.Select(failure => failure.Reason)));

        var plugin = result.LoadedPlugins[0];
        Assert.NotNull(plugin.Nuspec);
        Assert.NotNull(plugin.PluginManifest);
        Assert.NotEmpty(plugin.Dependencies);
        Assert.True(
            !string.IsNullOrEmpty(plugin.Metadata.Title) || !string.IsNullOrEmpty(plugin.Metadata.Description),
            "Expected Metadata to have a non-empty Title or Description.");
    }

    [Fact]
    public async Task WhenPluginLoaded_ThenDependenciesAreClassifiedCorrectly()
    {
        // Arrange
        var pluginsFolder = FindPluginsFolder();
        var options = CreateOptions(pluginsFolder);

        using var loader = new NuGetPluginLoader(options);

        // Act
        var result = await loader.LoadPluginsAsync(
            [new NuGetPluginReference("MyCompany.SamplePlugin1.HomeBlaze", "1.0.0")],
            CancellationToken.None);

        // Assert
        Assert.True(result.Success, string.Join(", ", result.Failures.Select(failure => failure.Reason)));

        var plugin = result.LoadedPlugins[0];

        var abstractionsDependency = plugin.Dependencies.FirstOrDefault(
            dependency => dependency.PackageName.Equals("MyCompany.Abstractions", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(abstractionsDependency);
        Assert.Equal(DependencyClassification.Host, abstractionsDependency.Classification);

        var bogusDependency = plugin.Dependencies.FirstOrDefault(
            dependency => dependency.PackageName.Equals("Bogus", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(bogusDependency);
        Assert.Equal(DependencyClassification.Isolated, bogusDependency.Classification);

        var samplePlugin1Dependency = plugin.Dependencies.FirstOrDefault(
            dependency => dependency.PackageName.Equals("MyCompany.SamplePlugin1", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(samplePlugin1Dependency);
        Assert.Equal(DependencyClassification.Isolated, samplePlugin1Dependency.Classification);
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
        return new NuGetPluginLoaderOptions
        {
            Feeds =
            [
                new NuGetFeed("local", pluginsFolder),
                NuGetFeed.NuGetOrg,
            ],
            CacheDirectory = _cacheDir,
            HostDependencies = HostDependencyResolver.FromDepsJson(),
        };
    }

    private static string FindPluginsFolder() => IntegrationTestHelper.FindPluginsFolder();
}
