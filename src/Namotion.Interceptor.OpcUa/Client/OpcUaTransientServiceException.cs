using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Thrown when a per-NodeId OPC UA operation (browse, browse-next, read) returns a
/// transient bad status during session work. The owning subject source catches this,
/// treats it as a load failure, and lets the session reconnect logic retry from scratch.
/// Permanent bad statuses do not raise this exception; they are logged and skipped.
/// </summary>
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
