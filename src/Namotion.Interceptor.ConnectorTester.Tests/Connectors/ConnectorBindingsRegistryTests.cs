using Xunit;
using Namotion.Interceptor.ConnectorTester.Connectors;

namespace Namotion.Interceptor.ConnectorTester.Tests.Connectors;

public class ConnectorBindingsRegistryTests
{
    [Theory]
    [InlineData(ConnectorKind.OpcUa,     4840)]
    [InlineData(ConnectorKind.Mqtt,      1883)]
    [InlineData(ConnectorKind.WebSocket, 8080)]
    public void WhenResolved_ThenDefaultPortMatchesConnector(ConnectorKind kind, int expectedPort)
    {
        // Arrange / Act
        var bindings = ConnectorBindingsRegistry.Resolve(kind);

        // Assert
        Assert.Equal(kind, bindings.Kind);
        Assert.Equal(expectedPort, bindings.DefaultPort);
    }
}
