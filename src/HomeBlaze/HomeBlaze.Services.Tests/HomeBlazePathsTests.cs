using Microsoft.Extensions.Configuration;
using Moq;

namespace HomeBlaze.Services.Tests;

public class HomeBlazePathsTests
{
    [Fact]
    public void WhenRootConfigFileIsNotSet_ThenDefaultsToDataRootJsonInWorkingDirectory()
    {
        // Arrange
        var configuration = CreateConfiguration(rootConfigFile: null, pluginConfigurationPath: null);

        // Act
        var path = HomeBlazePaths.GetRootConfigurationPath(configuration);

        // Assert
        Assert.Equal(Path.GetFullPath(Path.Combine("Data", "Root.json")), path);
    }

    [Fact]
    public void WhenConfigurationIsNull_ThenDefaultsToDataRootJsonInWorkingDirectory()
    {
        // Act
        var path = HomeBlazePaths.GetRootConfigurationPath(null);

        // Assert
        Assert.Equal(Path.GetFullPath(Path.Combine("Data", "Root.json")), path);
    }

    [Fact]
    public void WhenRootConfigFileIsAbsolute_ThenItIsUsedAsIs()
    {
        // Arrange
        var rootFile = Path.Combine(Path.GetTempPath(), "homeblaze-instance", "Root.json");
        var configuration = CreateConfiguration(rootFile, pluginConfigurationPath: null);

        // Act
        var path = HomeBlazePaths.GetRootConfigurationPath(configuration);

        // Assert
        Assert.Equal(rootFile, path);
    }

    [Fact]
    public void WhenPluginConfigurationPathIsNotSet_ThenDefaultsToFilesPluginsJsonInDataDirectory()
    {
        // Arrange
        var dataDirectory = Path.Combine(Path.GetTempPath(), "homeblaze-instance");
        var configuration = CreateConfiguration(Path.Combine(dataDirectory, "Root.json"), pluginConfigurationPath: null);

        // Act
        var path = HomeBlazePaths.GetPluginConfigurationPath(configuration);

        // Assert
        Assert.Equal(Path.Combine(dataDirectory, "Files", "Plugins.json"), path);
    }

    [Fact]
    public void WhenPluginConfigurationPathIsRelative_ThenResolvesAgainstDataDirectory()
    {
        // Arrange
        var dataDirectory = Path.Combine(Path.GetTempPath(), "homeblaze-instance");
        var configuration = CreateConfiguration(Path.Combine(dataDirectory, "Root.json"), "Other/Plugins.json");

        // Act
        var path = HomeBlazePaths.GetPluginConfigurationPath(configuration);

        // Assert
        Assert.Equal(Path.Combine(dataDirectory, "Other", "Plugins.json"), path);
    }

    [Fact]
    public void WhenPluginConfigurationPathIsAbsolute_ThenItIsUsedAsIs()
    {
        // Arrange
        var pluginFile = Path.Combine(Path.GetTempPath(), "elsewhere", "Plugins.json");
        var configuration = CreateConfiguration(rootConfigFile: null, pluginFile);

        // Act
        var path = HomeBlazePaths.GetPluginConfigurationPath(configuration);

        // Assert
        Assert.Equal(pluginFile, path);
    }

    private static IConfiguration CreateConfiguration(string? rootConfigFile, string? pluginConfigurationPath)
    {
        var configuration = new Mock<IConfiguration>();
        configuration.Setup(instance => instance[HomeBlazePaths.RootConfigurationFileKey]).Returns(rootConfigFile);
        configuration.Setup(instance => instance[HomeBlazePaths.PluginConfigurationPathKey]).Returns(pluginConfigurationPath);
        return configuration.Object;
    }
}
