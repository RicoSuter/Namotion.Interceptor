using Namotion.Interceptor.Connectors.Diagnostics;

namespace Namotion.Interceptor.OpcUa.Server;

/// <summary>
/// What the OPC UA server reports about its transport, on top of the shared connector diagnostics.
/// </summary>
/// <remarks>
/// When <see cref="ConnectorDiagnostics.IsOperational"/> has a value, it means the server has
/// started and is accepting client connections. A <c>null</c> value means the server has not
/// published liveness yet. <see cref="ConnectorDiagnostics.OperationalChangeTime"/> moves on every
/// internal restart, where <see cref="ConnectorDiagnostics.StartTime"/> marks the hosted service's
/// own start and does not.
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
    /// Gets the number of consecutive startup failures, reset on a successful start.
    /// </summary>
    public int ConsecutiveFailures => _server.ConsecutiveFailures;
}
