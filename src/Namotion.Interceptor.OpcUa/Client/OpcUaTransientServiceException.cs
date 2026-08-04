using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Thrown when a per-NodeId OPC UA operation (browse, browse-next, read) returns a
/// transient bad status during session work. The owning subject source treats this
/// as a load failure and lets the session reconnect logic retry from scratch.
/// Permanent bad statuses do not raise this exception; they are logged and skipped.
/// </summary>
/// <remarks>
/// External callers can observe instances of this type via <c>OpcUaClientDiagnostics.LastError</c>
/// and through the hosting framework's error pipeline when initial connect or reconnect fails.
/// The type is public so consumers can branch on it (vs. fatal exceptions) when deciding how
/// to surface OPC UA errors in their own observability surface.
/// </remarks>
public sealed class OpcUaTransientServiceException : Exception
{
    public string Operation { get; }

    public NodeId? NodeId { get; }

    public StatusCode StatusCode { get; }

    public OpcUaTransientServiceException(string operation, NodeId? nodeId, StatusCode statusCode)
        : base($"OPC UA {operation} returned transient status {statusCode} for NodeId {nodeId}. The load will be aborted and retried via session reconnect.")
    {
        Operation = operation;
        NodeId = nodeId;
        StatusCode = statusCode;
    }
}
