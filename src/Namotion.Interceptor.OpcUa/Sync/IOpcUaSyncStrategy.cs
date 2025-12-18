using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Sync;

/// <summary>
/// Strategy interface for OPC UA address space synchronization.
/// Implementations handle either client-side or server-side synchronization logic.
/// </summary>
public interface IOpcUaSyncStrategy
{
    /// <summary>
    /// Called when a local subject is attached to the object graph.
    /// Implementations should create corresponding OPC UA nodes/monitored items.
    /// </summary>
    Task OnSubjectAttachedAsync(IInterceptorSubject subject, CancellationToken cancellationToken);

    /// <summary>
    /// Called when a local subject is detached from the object graph.
    /// Implementations should remove corresponding OPC UA nodes/monitored items.
    /// </summary>
    Task OnSubjectDetachedAsync(IInterceptorSubject subject, CancellationToken cancellationToken);

    /// <summary>
    /// Called when a remote OPC UA node is added.
    /// Implementations should create a corresponding local subject and attach it.
    /// </summary>
    Task OnRemoteNodeAddedAsync(ReferenceDescription node, NodeId parentNodeId, CancellationToken cancellationToken);

    /// <summary>
    /// Called when a remote OPC UA node is removed.
    /// Implementations should find and detach the corresponding local subject.
    /// </summary>
    Task OnRemoteNodeRemovedAsync(NodeId nodeId, CancellationToken cancellationToken);

    /// <summary>
    /// Browses a node to get its children.
    /// Used by both client (to browse server) and server (to check local state).
    /// </summary>
    Task<ReferenceDescriptionCollection> BrowseNodeAsync(NodeId nodeId, CancellationToken cancellationToken);
}
