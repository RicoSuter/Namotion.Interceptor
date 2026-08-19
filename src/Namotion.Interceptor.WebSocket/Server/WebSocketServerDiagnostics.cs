using Namotion.Interceptor.Connectors.Diagnostics;

namespace Namotion.Interceptor.WebSocket.Server;

/// <summary>
/// What the WebSocket server reports about its transport, on top of the shared connector diagnostics.
/// </summary>
/// <remarks>
/// <see cref="ConnectorDiagnostics.IsOperational"/> means the listener is accepting connections.
/// Neither throughput direction is measured, so both rates are <c>null</c> rather than 0.
/// The four counters mirrored from
/// <see cref="Namotion.Interceptor.Connectors.Updates.SubjectUpdateDiagnostics"/> are process-wide,
/// not per server instance, and are the only production signal that the update pipeline is dropping
/// or falling back; values that keep rising without settling under structural churn are the alert.
/// </remarks>
public sealed class WebSocketServerDiagnostics : ConnectorDiagnostics
{
    private readonly WebSocketSubjectServer _server;

    internal WebSocketServerDiagnostics(WebSocketSubjectServer server, ConnectorMetrics metrics)
        : base(metrics)
    {
        _server = server;
    }

    /// <summary>
    /// Gets the number of currently connected WebSocket clients.
    /// </summary>
    public int ConnectionCount => _server.ConnectionCount;

    /// <summary>
    /// Gets the sequence number most recently assigned to an outgoing message, a monotonic position
    /// in the message stream rather than a count of events.
    /// </summary>
    public long CurrentSequence => _server.CurrentSequence;

    /// <inheritdoc cref="Namotion.Interceptor.Connectors.Updates.SubjectUpdateDiagnostics.DroppedOutboundChanges" />
    public long DroppedOutboundChanges => Namotion.Interceptor.Connectors.Updates.SubjectUpdateDiagnostics.DroppedOutboundChanges;

    /// <inheritdoc cref="Namotion.Interceptor.Connectors.Updates.SubjectUpdateDiagnostics.MetadataFallbackSerializations" />
    public long MetadataFallbackSerializations => Namotion.Interceptor.Connectors.Updates.SubjectUpdateDiagnostics.MetadataFallbackSerializations;

    /// <inheritdoc cref="Namotion.Interceptor.Connectors.Updates.SubjectUpdateDiagnostics.DroppedInboundSubjectUpdates" />
    public long DroppedInboundSubjectUpdates => Namotion.Interceptor.Connectors.Updates.SubjectUpdateDiagnostics.DroppedInboundSubjectUpdates;

    /// <inheritdoc cref="Namotion.Interceptor.Connectors.Updates.SubjectUpdateDiagnostics.UnknownInboundProperties" />
    public long UnknownInboundProperties => Namotion.Interceptor.Connectors.Updates.SubjectUpdateDiagnostics.UnknownInboundProperties;
}
