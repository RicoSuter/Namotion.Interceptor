using Namotion.Interceptor.ConnectorTester.Connectors.Mqtt;
using Namotion.Interceptor.ConnectorTester.Connectors.OpcUa;
using Namotion.Interceptor.ConnectorTester.Connectors.WebSocket;

namespace Namotion.Interceptor.ConnectorTester.Connectors;

public static class ConnectorBindingsRegistry
{
    public static IConnectorBindings Resolve(ConnectorKind kind) => kind switch
    {
        ConnectorKind.OpcUa     => new OpcUaConnectorBindings(),
        ConnectorKind.Mqtt      => new MqttConnectorBindings(),
        ConnectorKind.WebSocket => new WebSocketConnectorBindings(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown ConnectorKind.")
    };
}
