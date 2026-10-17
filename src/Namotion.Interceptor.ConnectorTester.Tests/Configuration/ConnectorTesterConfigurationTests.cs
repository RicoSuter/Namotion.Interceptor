using Xunit;
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
