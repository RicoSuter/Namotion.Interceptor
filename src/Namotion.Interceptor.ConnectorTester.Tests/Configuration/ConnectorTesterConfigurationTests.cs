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
}
