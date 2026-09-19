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
    [InlineData(4, 0)] // five participants for four value properties
    [InlineData(1, 10)] // batch mutation
    public void WhenDisjointPropertiesIsCombinedWithAnUnsupportedSetting_ThenValidateThrows(int clientCount, int numberOfBatches)
    {
        // Arrange
        var configuration = CreateConfiguration(disjointProperties: true, clientCount, numberOfBatches);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(configuration.ValidateDisjointProperties);
    }

    [Theory]
    [InlineData(false, 4, 10)] // disabled, so batch mutation and five participants are fine
    [InlineData(true, 3, 0)] // four participants for four value properties
    public void WhenDisjointPropertiesIsDisabledOrWithinLimits_ThenValidateDoesNotThrow(
        bool disjointProperties, int clientCount, int numberOfBatches)
    {
        // Arrange
        var configuration = CreateConfiguration(disjointProperties, clientCount, numberOfBatches);

        // Act
        var exception = Record.Exception(configuration.ValidateDisjointProperties);

        // Assert
        Assert.Null(exception);
    }

    private static ConnectorTesterConfiguration CreateConfiguration(
        bool disjointProperties, int clientCount, int numberOfBatches) => new()
    {
        DisjointProperties = disjointProperties,
        NumberOfBatches = numberOfBatches,
        Clients = Enumerable.Range(0, clientCount)
            .Select(index => new ParticipantConfiguration { Name = $"client-{index}" })
            .ToList()
    };
}
