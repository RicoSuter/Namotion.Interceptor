using Xunit;

using HomeBlaze.Plugins;
using Namotion.NuGet.Plugins.Configuration;

namespace HomeBlaze.Plugins.Tests;

public class PluginConfigurationTests
{
    private const string ValidJson = """
        {
          "feeds": [
            { "name": "nuget.org", "url": "https://api.nuget.org/v3/index.json" },
            { "name": "private", "url": "https://pkgs.dev.azure.com/myorg/feed", "apiKey": "secret123" }
          ],
          "hostPackages": [
            "MyCompany.*.Abstractions",
            "Exact.Package"
          ],
          "plugins": [
            { "packageName": "Plugin.One", "version": "1.0.0" },
            { "packageName": "Plugin.Two", "version": "2.0.0" }
          ]
        }
        """;

    [Fact]
    public void WhenLoadingValidJson_ThenParsesAllFields()
    {
        // Arrange
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(ValidJson));

        // Act
        var config = PluginConfiguration.LoadFrom(stream);

        // Assert
        Assert.Equal(2, config.Feeds.Count);
        Assert.Equal("nuget.org", config.Feeds[0].Name);
        Assert.Equal("secret123", config.Feeds[1].ApiKey);
        Assert.Equal(2, config.HostPackages.Count);
        Assert.Equal("MyCompany.*.Abstractions", config.HostPackages[0]);
        Assert.Equal(2, config.Plugins.Count);
        Assert.Equal("Plugin.One", config.Plugins[0].PackageName);
        Assert.Equal("Plugin.Two", config.Plugins[1].PackageName);
    }

    [Fact]
    public void WhenConvertingToLoaderOptions_ThenMapsCorrectly()
    {
        // Arrange
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(ValidJson));
        var config = PluginConfiguration.LoadFrom(stream);
        var hostDependencies = HostDependencyResolver.FromAssemblies(("SomeLib", new Version(1, 0, 0)));

        // Act
        var options = config.ToLoaderOptions(hostDependencies);

        // Assert
        Assert.Equal(2, options.Feeds.Count);
        Assert.NotNull(options.IsHostPackage);
        Assert.True(options.IsHostPackage!("MyCompany.Devices.Abstractions"));
        Assert.False(options.IsHostPackage!("Unrelated.Package"));
        Assert.Same(hostDependencies, options.HostDependencies);
    }

    [Fact]
    public void WhenJsonHasNoFeeds_ThenDefaultsToNuGetOrg()
    {
        // Arrange
        var json = """{ "plugins": [{ "packageName": "Foo", "version": "1.0.0" }] }""";
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));

        // Act
        var config = PluginConfiguration.LoadFrom(stream);

        // Assert
        Assert.Single(config.NuGetFeeds);
        Assert.Equal("nuget.org", config.NuGetFeeds[0].Name);
    }

    [Fact]
    public void WhenJsonHasNoHostPackages_ThenDefaultsToEmpty()
    {
        // Arrange
        var json = """{ "plugins": [{ "packageName": "Foo", "version": "1.0.0" }] }""";
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));

        // Act
        var config = PluginConfiguration.LoadFrom(stream);

        // Assert
        Assert.Empty(config.HostPackages);
    }

    [Fact]
    public void WhenLoadingFromFile_ThenWorks()
    {
        // Arrange
        var path = Path.GetTempFileName();
        File.WriteAllText(path, ValidJson);

        try
        {
            // Act
            var config = PluginConfiguration.LoadFrom(path);

            // Assert
            Assert.Equal(2, config.Plugins.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
