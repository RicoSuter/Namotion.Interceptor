using Namotion.Interceptor.ConnectorTester.Connectors.Mqtt;
using Namotion.Interceptor.ConnectorTester.Connectors.OpcUa;
using Namotion.Interceptor.ConnectorTester.Connectors.WebSocket;

namespace Namotion.Interceptor.ConnectorTester.Connectors;

public static class ConnectorFactoryRegistry
{
    public static IConnectorFactory Resolve(ConnectorKind kind) => kind switch
    {
        ConnectorKind.OpcUa     => new OpcUaConnectorFactory(),
        ConnectorKind.Mqtt      => new MqttConnectorFactory(),
        ConnectorKind.WebSocket => new WebSocketConnectorFactory(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown ConnectorKind.")
    };
}
