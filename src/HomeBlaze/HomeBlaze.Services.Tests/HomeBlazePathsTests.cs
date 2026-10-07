using Microsoft.Extensions.Configuration;
using Moq;

namespace HomeBlaze.Services.Tests;

public class HomeBlazePathsTests
{
    [Fact]
    public void WhenRootConfigFileIsNotSet_ThenDefaultsToDataRootJsonInWorkingDirectory()
    {
        // Arrange
        var configuration = CreateConfiguration(rootConfigFile: null);

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
    public void WhenRootConfigFileIsEmpty_ThenDefaultsToDataRootJsonInWorkingDirectory()
    {
        // Arrange
        var configuration = CreateConfiguration(rootConfigFile: "   ");

        // Act
        var path = HomeBlazePaths.GetRootConfigurationPath(configuration);

        // Assert
        Assert.Equal(Path.GetFullPath(Path.Combine("Data", "Root.json")), path);
    }

    [Fact]
    public void WhenRootConfigFileIsAbsolute_ThenItIsUsedAsIs()
    {
        // Arrange
        var rootFile = Path.Combine(Path.GetTempPath(), "homeblaze-instance", "Root.json");
        var configuration = CreateConfiguration(rootFile);

        // Act
        var path = HomeBlazePaths.GetRootConfigurationPath(configuration);

        // Assert
        Assert.Equal(rootFile, path);
    }

    private static IConfiguration CreateConfiguration(string? rootConfigFile)
    {
        var configuration = new Mock<IConfiguration>();
        configuration.Setup(instance => instance[HomeBlazePaths.RootConfigurationFileKey]).Returns(rootConfigFile);
        return configuration.Object;
    }
}
