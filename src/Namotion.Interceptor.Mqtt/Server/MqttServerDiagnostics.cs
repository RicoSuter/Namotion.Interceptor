using Namotion.Interceptor.Connectors.Diagnostics;

namespace Namotion.Interceptor.Mqtt.Server;

/// <summary>
/// What the MQTT server reports about its transport, on top of the shared connector diagnostics.
/// </summary>
/// <remarks>
/// <see cref="ConnectorDiagnostics.IsOperational"/> means the broker is listening. Neither
/// throughput direction is measured, so both rates are <c>null</c> rather than 0.
/// </remarks>
public sealed class MqttServerDiagnostics : ConnectorDiagnostics
{
    private readonly MqttSubjectServer _server;

    internal MqttServerDiagnostics(MqttSubjectServer server, ConnectorMetrics metrics)
        : base(metrics)
    {
        _server = server;
    }

    /// <summary>
    /// Gets the number of clients currently connected to the broker.
    /// </summary>
    public int ConnectedClientCount => _server.ConnectedClientCount;
}
