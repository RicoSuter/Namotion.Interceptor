using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Server;

namespace Namotion.Interceptor.OpcUa.Server.Graph;

/// <summary>
/// Queues and emits model change events.
/// Extracted from CustomNodeManager for better separation of concerns.
/// </summary>
internal class ModelChangePublisher
{
    private readonly object _pendingModelChangesLock = new();
    private List<ModelChangeStructureDataType> _pendingModelChanges = new();
    private readonly ILogger _logger;

    public ModelChangePublisher(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Queues a model change for batched emission.
    /// Changes are emitted when Flush is called.
    /// Thread-safe: uses lock for synchronization.
    /// </summary>
    /// <param name="affectedNodeId">The NodeId of the affected node.</param>
    /// <param name="verb">The type of change (NodeAdded, NodeDeleted, ReferenceAdded, etc.).</param>
    public void QueueChange(NodeId affectedNodeId, ModelChangeStructureVerbMask verb)
    {
        lock (_pendingModelChangesLock)
        {
            _pendingModelChanges.Add(new ModelChangeStructureDataType
            {
                Affected = affectedNodeId,
                AffectedType = null, // Optional: could be set to TypeDefinitionId for added nodes
                Verb = (byte)verb
            });
        }
    }

    /// <summary>
    /// Flushes all pending model change events to clients.
    /// Emits a GeneralModelChangeEvent containing all batched changes.
    /// Called after a batch of structural changes has been processed.
    /// Thread-safe: uses atomic swap pattern to capture pending changes.
    /// </summary>
    /// <param name="server">The OPC UA server to report events on.</param>
    /// <param name="systemContext">The system context for event initialization.</param>
    public void Flush(IServerInternal server, ISystemContext systemContext)
    {
        // Atomically swap the pending changes list with a new empty list
        // This ensures thread-safety without holding the lock during event emission
        List<ModelChangeStructureDataType> changesToEmit;
        lock (_pendingModelChangesLock)
        {
            if (_pendingModelChanges.Count == 0)
            {
                return;
            }

            changesToEmit = _pendingModelChanges;
            _pendingModelChanges = new List<ModelChangeStructureDataType>();
        }

        try
        {
            // Create and emit the GeneralModelChangeEvent
            var eventState = new GeneralModelChangeEventState(null);
            eventState.Initialize(
                systemContext,
                null,
                EventSeverity.Medium,
                new LocalizedText($"Address space changed: {changesToEmit.Count} modification(s)"));

            eventState.Changes = new PropertyState<ModelChangeStructureDataType[]>(eventState)
            {
                Value = changesToEmit.ToArray()
            };

            // Report the event on the Server node (standard location for model change events)
            eventState.SetChildValue(systemContext, BrowseNames.SourceNode, ObjectIds.Server, false);
            eventState.SetChildValue(systemContext, BrowseNames.SourceName, "AddressSpace", false);

            // Note: The event will be distributed to subscribed clients via the server's event queue
            server.ReportEvent(systemContext, eventState);

            _logger.LogInformation("Emitted GeneralModelChangeEvent with {ChangeCount} changes.", changesToEmit.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to emit GeneralModelChangeEvent. Continuing without event notification.");
        }
    }
}
