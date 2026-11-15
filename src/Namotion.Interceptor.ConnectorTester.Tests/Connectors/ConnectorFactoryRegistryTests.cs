using Xunit;
using Namotion.Interceptor.ConnectorTester.Configuration;
using Namotion.Interceptor.ConnectorTester.Connectors;

namespace Namotion.Interceptor.ConnectorTester.Tests.Connectors;

public class ConnectorFactoryRegistryTests
{
    [Theory]
    [InlineData(ConnectorKind.OpcUa,     4840)]
    [InlineData(ConnectorKind.Mqtt,      1883)]
    [InlineData(ConnectorKind.WebSocket, 8080)]
    public void WhenResolved_ThenDefaultPortMatchesConnector(ConnectorKind kind, int expectedPort)
    {
        // Arrange
        var configuration = new ConnectorTesterConfiguration { Connector = kind.ToString() };

        // Act
        var connectorFactory = ConnectorFactoryRegistry.Resolve(configuration, "logs");

        // Assert
        Assert.Equal(kind, connectorFactory.Kind);
        Assert.Equal(expectedPort, connectorFactory.DefaultPort);
    }
}
