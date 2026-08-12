using Namotion.Interceptor.Connectors.Diagnostics;

namespace Namotion.Interceptor.OpcUa.Server;

/// <summary>
/// What the OPC UA server reports about its transport, on top of the shared connector diagnostics.
/// </summary>
/// <remarks>
/// <see cref="ConnectorDiagnostics.IsOperational"/> means the server has started and is accepting
/// client connections. It replaces the former <c>IsRunning</c>, and
/// <see cref="ConnectorDiagnostics.OperationalChangeTime"/> replaces the former <c>StartTime</c> and
/// <c>Uptime</c>: it moves on every internal restart, where
/// <see cref="ConnectorDiagnostics.StartTime"/> does not.
/// </remarks>
public sealed class OpcUaServerDiagnostics : ConnectorDiagnostics
{
    private readonly OpcUaSubjectServer _server;

    internal OpcUaServerDiagnostics(OpcUaSubjectServer server, ConnectorMetrics metrics)
        : base(metrics)
    {
        _server = server;
    }

    /// <summary>
    /// Gets the number of currently active client sessions.
    /// </summary>
    public int ActiveSessionCount => _server.ActiveSessionCount;

    /// <summary>
    /// Gets the number of consecutive startup failures. A gauge that resets on a successful start,
    /// which is why it carries no <c>Total</c> prefix.
    /// </summary>
    public int ConsecutiveFailures => _server.ConsecutiveFailures;
}
