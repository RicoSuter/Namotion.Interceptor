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
    public void WhenRootConfigFileIsEmpty_ThenDefaultsToDataRootJsonInWorkingDirectory()
    {
        // Arrange
        var configuration = CreateConfiguration(rootConfigFile: "   ", pluginConfigurationPath: null);

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
    public void WhenPluginConfigurationPathIsEmpty_ThenDefaultsToFilesPluginsJsonInDataDirectory()
    {
        // Arrange
        var dataDirectory = Path.Combine(Path.GetTempPath(), "homeblaze-instance");
        var configuration = CreateConfiguration(Path.Combine(dataDirectory, "Root.json"), pluginConfigurationPath: "   ");

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

    [Fact]
    public void WhenRootConfigFileIsSet_ThenDataProtectionKeysAreNextToIt()
    {
        // Arrange
        var dataDirectory = Path.Combine(Path.GetTempPath(), "homeblaze-instance");
        var configuration = CreateConfiguration(Path.Combine(dataDirectory, "Root.json"), pluginConfigurationPath: null);

        // Act
        var path = HomeBlazePaths.GetDataProtectionKeysPath(configuration);

        // Assert
        Assert.Equal(Path.Combine(dataDirectory, "DataProtection-Keys"), path);
    }

    [Fact]
    public void WhenRootConfigFileIsNotSet_ThenDataProtectionKeysAreInDataDirectoryOfWorkingDirectory()
    {
        // Arrange
        var configuration = CreateConfiguration(rootConfigFile: null, pluginConfigurationPath: null);

        // Act
        var path = HomeBlazePaths.GetDataProtectionKeysPath(configuration);

        // Assert
        Assert.Equal(Path.GetFullPath(Path.Combine("Data", "DataProtection-Keys")), path);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void WhenDataProtectionCertificatePathIsNotSet_ThenReturnsNull(string? certificatePath)
    {
        // Arrange
        var configuration = CreateConfiguration(rootConfigFile: null, pluginConfigurationPath: null, certificatePath);

        // Act
        var path = HomeBlazePaths.GetDataProtectionCertificatePath(configuration);

        // Assert
        Assert.Null(path);
    }

    [Fact]
    public void WhenDataProtectionCertificatePathIsAbsolute_ThenItIsUsedAsIs()
    {
        // Arrange
        var certificateFile = Path.Combine(Path.GetTempPath(), "secrets", "data-protection.pfx");
        var configuration = CreateConfiguration(rootConfigFile: null, pluginConfigurationPath: null, certificateFile);

        // Act
        var path = HomeBlazePaths.GetDataProtectionCertificatePath(configuration);

        // Assert
        Assert.Equal(certificateFile, path);
    }

    [Fact]
    public void WhenDataProtectionCertificatePathIsRelative_ThenResolvesAgainstWorkingDirectory()
    {
        // Arrange
        var dataDirectory = Path.Combine(Path.GetTempPath(), "homeblaze-instance");
        var configuration = CreateConfiguration(Path.Combine(dataDirectory, "Root.json"), pluginConfigurationPath: null, "secrets/data-protection.pfx");

        // Act
        var path = HomeBlazePaths.GetDataProtectionCertificatePath(configuration);

        // Assert
        Assert.Equal(Path.GetFullPath(Path.Combine("secrets", "data-protection.pfx")), path);
    }

    private static IConfiguration CreateConfiguration(string? rootConfigFile, string? pluginConfigurationPath, string? dataProtectionCertificatePath = null)
    {
        var configuration = new Mock<IConfiguration>();
        configuration.Setup(instance => instance[HomeBlazePaths.DataProtectionCertificatePathKey]).Returns(dataProtectionCertificatePath);
        configuration.Setup(instance => instance[HomeBlazePaths.RootConfigurationFileKey]).Returns(rootConfigFile);
        configuration.Setup(instance => instance[HomeBlazePaths.PluginConfigurationPathKey]).Returns(pluginConfigurationPath);
        return configuration.Object;
    }
}
