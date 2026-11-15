using Xunit;
using Microsoft.Extensions.Configuration;
using Namotion.Interceptor.ConnectorTester.Configuration;
using Namotion.Interceptor.ConnectorTester.Connectors;

namespace Namotion.Interceptor.ConnectorTester.Tests.Configuration;

public class ConnectorTesterConfigurationTests
{
    [Theory]
    [InlineData("opcua",     ConnectorKind.OpcUa)]
    [InlineData("OpcUa",     ConnectorKind.OpcUa)]
    [InlineData("OPCUA",     ConnectorKind.OpcUa)]
    [InlineData("mqtt",      ConnectorKind.Mqtt)]
    [InlineData("MQTT",      ConnectorKind.Mqtt)]
    [InlineData("websocket", ConnectorKind.WebSocket)]
    [InlineData("WebSocket", ConnectorKind.WebSocket)]
    public void WhenConnectorStringValid_ThenConnectorKindParses(string connectorString, ConnectorKind expectedKind)
    {
        // Arrange
        var configuration = new ConnectorTesterConfiguration { Connector = connectorString };

        // Act
        var kind = configuration.ConnectorKind;

        // Assert
        Assert.Equal(expectedKind, kind);
    }

    [Fact]
    public void WhenConnectorStringInvalid_ThenConnectorKindThrows()
    {
        // Arrange
        var configuration = new ConnectorTesterConfiguration { Connector = "not-a-connector" };

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => _ = configuration.ConnectorKind);
    }

    [Theory]
    [InlineData(0)] // random mutation
    [InlineData(10)] // batch mutation
    public void WhenVerifyWriteDurabilityHasMoreParticipantsThanValueProperties_ThenValidateThrows(int numberOfBatches)
    {
        // Arrange
        var configuration = CreateConfiguration(verifyWriteDurability: true, clientCount: 4, numberOfBatches);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(configuration.ValidateVerifyWriteDurability);
    }

    [Theory]
    [InlineData(false, 4, 0)] // disabled, so five participants are fine
    [InlineData(false, 4, 10)]
    [InlineData(true, 3, 0)] // four participants for four value properties
    [InlineData(true, 3, 10)]
    public void WhenVerifyWriteDurabilityIsDisabledOrWithinLimits_ThenValidateDoesNotThrow(
        bool verifyWriteDurability, int clientCount, int numberOfBatches)
    {
        // Arrange
        var configuration = CreateConfiguration(verifyWriteDurability, clientCount, numberOfBatches);

        // Act
        var exception = Record.Exception(configuration.ValidateVerifyWriteDurability);

        // Assert
        Assert.Null(exception);
    }

    [Theory]
    [InlineData(-1)] // server
    [InlineData(0)] // first client
    public void WhenVerifyWriteDurabilityHasStructuralMutations_ThenValidateThrowsNamingTheParticipant(int clientIndex)
    {
        // Arrange
        var configuration = CreateConfiguration(verifyWriteDurability: true, clientCount: 2, numberOfBatches: 0);
        var participant = clientIndex < 0 ? configuration.Server : configuration.Clients[clientIndex];
        participant.StructuralMutationRate = 1;

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(configuration.ValidateVerifyWriteDurability);
        Assert.Contains($"'{participant.Name}'", exception.Message);
    }

    [Fact]
    public void WhenVerifyWriteDurabilityIsDisabled_ThenStructuralMutationsAreFine()
    {
        // Arrange
        var configuration = CreateConfiguration(verifyWriteDurability: false, clientCount: 1, numberOfBatches: 0);
        configuration.Clients[0].StructuralMutationRate = 1;

        // Act
        var exception = Record.Exception(configuration.ValidateVerifyWriteDurability);

        // Assert
        Assert.Null(exception);
    }

    [Theory]
    [InlineData("opcua-chaos", "opcua-durability")]
    [InlineData("websocket-chaos", "websocket-durability")]
    public void WhenDurabilityProfileIsLoaded_ThenItMatchesItsChaosProfileWithTheCheckOn(
        string chaosProfile, string durabilityProfile)
    {
        // Arrange
        var chaosSettings = LoadProfile(chaosProfile);
        var durabilitySettings = LoadProfile(durabilityProfile);

        // Act
        var addedSettings = durabilitySettings.Except(chaosSettings).ToDictionary();
        var removedSettings = chaosSettings.Except(durabilitySettings).ToDictionary();

        // Assert
        Assert.Equal(new Dictionary<string, string?> { ["ConnectorTester:VerifyWriteDurability"] = "True" }, addedSettings);
        Assert.Empty(removedSettings);
    }

    private static Dictionary<string, string?> LoadProfile(string environmentName)
        => new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile($"appsettings.{environmentName}.json", optional: false)
            .Build()
            .AsEnumerable()
            .ToDictionary();

    private static ConnectorTesterConfiguration CreateConfiguration(
        bool verifyWriteDurability, int clientCount, int numberOfBatches) => new()
    {
        VerifyWriteDurability = verifyWriteDurability,
        NumberOfBatches = numberOfBatches,
        Clients = Enumerable.Range(0, clientCount)
            .Select(index => new ParticipantConfiguration { Name = $"client-{index}" })
            .ToList()
    };
}
