using Microsoft.Extensions.Configuration;
using Namotion.Interceptor.ConnectorTester.Configuration;
using Opc.Ua;
using Xunit;

namespace Namotion.Interceptor.ConnectorTester.Tests.Configuration;

public class OpcUaConfigurationTests
{
    [Fact]
    public void WhenOpcUaSectionIsMissing_ThenClientsConnectUnsecured()
    {
        // Arrange
        var configurationRoot = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectorTester:Connector"] = "opcua" })
            .Build();

        // Act
        var configuration = configurationRoot.GetSection("ConnectorTester").Get<ConnectorTesterConfiguration>()!;

        // Assert
        Assert.Equal(MessageSecurityMode.None, configuration.OpcUa.MinimumSecurityMode);
        Assert.False(configuration.OpcUa.IsSecured);
    }

    [Fact]
    public void WhenMinimumSecurityModeIsConfigured_ThenItBindsAndIsSecured()
    {
        // Arrange
        var configurationRoot = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectorTester:OpcUa:MinimumSecurityMode"] = "SignAndEncrypt"
            })
            .Build();

        // Act
        var configuration = configurationRoot.GetSection("ConnectorTester").Get<ConnectorTesterConfiguration>()!;

        // Assert
        Assert.Equal(MessageSecurityMode.SignAndEncrypt, configuration.OpcUa.MinimumSecurityMode);
        Assert.True(configuration.OpcUa.IsSecured);
    }

    [Fact]
    public void WhenSecuredChaosProfileIsLoaded_ThenItMatchesTheChaosProfileExceptForSecurity()
    {
        // Arrange
        var chaosSettings = LoadProfile("opcua-chaos");
        var securedSettings = LoadProfile("opcua-chaos-secured");

        // Act
        var chaosSettingsWithoutOpcUa = chaosSettings.Where(setting => !IsOpcUaSetting(setting.Key)).ToDictionary();
        var securedSettingsWithoutOpcUa = securedSettings.Where(setting => !IsOpcUaSetting(setting.Key)).ToDictionary();
        var securedOpcUaSettings = securedSettings.Where(setting => IsOpcUaSetting(setting.Key)).ToDictionary();

        // Assert
        Assert.Equal(chaosSettingsWithoutOpcUa, securedSettingsWithoutOpcUa);
        Assert.Equal(
            new Dictionary<string, string?>
            {
                ["ConnectorTester:OpcUa"] = null,
                ["ConnectorTester:OpcUa:MinimumSecurityMode"] = "SignAndEncrypt"
            },
            securedOpcUaSettings);
    }

    private static bool IsOpcUaSetting(string key)
        => key == "ConnectorTester:OpcUa" || key.StartsWith("ConnectorTester:OpcUa:", StringComparison.Ordinal);

    private static Dictionary<string, string?> LoadProfile(string environmentName)
        => new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile($"appsettings.{environmentName}.json", optional: false)
            .Build()
            .AsEnumerable()
            .ToDictionary();
}
