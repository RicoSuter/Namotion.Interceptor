using Namotion.Interceptor.Tracking.Lifecycle;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Sync;

/// <summary>
/// Strategy interface for OPC UA address space synchronization.
/// Implementations handle either client-side or server-side synchronization logic.
/// </summary>
public interface IOpcUaSyncStrategy
{
    /// <summary>
    /// Initializes the strategy with the root subject and its corresponding NodeId.
    /// Must be called before any sync operations.
    /// </summary>
    void Initialize(IInterceptorSubject rootSubject, NodeId rootNodeId);

    /// <summary>
    /// Called when a local subject is attached to the object graph.
    /// Implementations should create corresponding OPC UA nodes/monitored items.
    /// </summary>
    /// <param name="change">The lifecycle change containing subject, parent property, and index information.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task OnSubjectAttachedAsync(SubjectLifecycleChange change, CancellationToken cancellationToken);

    /// <summary>
    /// Called when a local subject is detached from the object graph.
    /// Implementations should remove corresponding OPC UA nodes/monitored items.
    /// </summary>
    /// <param name="change">The lifecycle change containing subject, parent property, and index information.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task OnSubjectDetachedAsync(SubjectLifecycleChange change, CancellationToken cancellationToken);

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

    /// <summary>
    /// Ensures a subject is unregistered from internal mappings (idempotent).
    /// Called in finally blocks to prevent memory leaks even when exceptions occur.
    /// </summary>
    void EnsureUnregistered(IInterceptorSubject subject);

    /// <summary>
    /// Clears all internal Subject ↔ NodeId mappings.
    /// Called during dispose or full reconnection.
    /// </summary>
    void ClearAllMappings();
}
