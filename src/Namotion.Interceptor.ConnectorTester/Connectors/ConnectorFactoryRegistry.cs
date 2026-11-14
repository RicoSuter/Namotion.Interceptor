using Namotion.Interceptor.ConnectorTester.Configuration;
using Namotion.Interceptor.ConnectorTester.Connectors.Mqtt;
using Namotion.Interceptor.ConnectorTester.Connectors.OpcUa;
using Namotion.Interceptor.ConnectorTester.Connectors.WebSocket;

namespace Namotion.Interceptor.ConnectorTester.Connectors;

public static class ConnectorFactoryRegistry
{
    /// <summary>Creates the factory for the configured connector.</summary>
    /// <param name="configuration">The tester configuration.</param>
    /// <param name="runDirectory">The run's output directory, which also holds the OPC UA certificate stores when secured.</param>
    public static IConnectorFactory Resolve(ConnectorTesterConfiguration configuration, string runDirectory) => configuration.ConnectorKind switch
    {
        ConnectorKind.OpcUa     => new OpcUaConnectorFactory(configuration.OpcUa, Path.GetFullPath(Path.Combine(runDirectory, "pki"))),
        ConnectorKind.Mqtt      => new MqttConnectorFactory(),
        ConnectorKind.WebSocket => new WebSocketConnectorFactory(),
        _ => throw new ArgumentOutOfRangeException(nameof(configuration), configuration.ConnectorKind, "Unknown ConnectorKind.")
    };
}
