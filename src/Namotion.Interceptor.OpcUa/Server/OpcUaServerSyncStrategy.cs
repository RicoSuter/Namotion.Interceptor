using Microsoft.Extensions.Logging;
using Namotion.Interceptor.OpcUa.Sync;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;
using Opc.Ua.Server;

namespace Namotion.Interceptor.OpcUa.Server;

/// <summary>
/// Server-side implementation of OPC UA address space synchronization.
/// Handles creating OPC UA nodes for local subjects and firing ModelChangeEvents for remote clients.
/// </summary>
internal class OpcUaServerSyncStrategy : IOpcUaSyncStrategy
{
    private readonly OpcUaServerConfiguration _configuration;
    private readonly OpcUaSubjectServerBackgroundService _serverService;
    private readonly ILogger _logger;
    private readonly Dictionary<NodeId, IInterceptorSubject> _nodeIdToSubject = new();
    private readonly Dictionary<IInterceptorSubject, NodeId> _subjectToNodeId = new();

    private CustomNodeManager? _nodeManager;
    private IServerInternal? _server;

    public OpcUaServerSyncStrategy(
        OpcUaServerConfiguration configuration,
        OpcUaSubjectServerBackgroundService serverService,
        ILogger logger)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _serverService = serverService ?? throw new ArgumentNullException(nameof(serverService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Sets the node manager and server. Must be called before any sync operations.
    /// </summary>
    public void SetNodeManager(CustomNodeManager? nodeManager, IServerInternal? server)
    {
        _nodeManager = nodeManager;
        _server = server;
    }

    public async Task OnSubjectAttachedAsync(IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        var registeredSubject = subject.TryGetRegisteredSubject();
        if (registeredSubject is null || _nodeManager is null || _server is null)
        {
            return;
        }

        _logger.LogDebug(
            "Server: Subject attached - {SubjectType}. Creating OPC UA nodes...",
            subject.GetType().Name);

        try
        {
            // Create OPC UA nodes for this subject dynamically
            // Note: CustomNodeManager's CreateObjectNode method already handles subject node creation
            // The challenge is that we need to trigger it for dynamically attached subjects
            // For now, we log that the subject was attached and track it for future node creation
            
            _logger.LogInformation(
                "Subject attached: {SubjectType}. Dynamic node creation requires CustomNodeManager integration.",
                subject.GetType().Name);

            // Fire ModelChangeEvent to notify connected clients
            if (_configuration.EnableLiveSync && _configuration.EnableRemoteNodeManagement)
            {
                await FireModelChangeEventAsync(ModelChangeStructureVerbMask.NodeAdded, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle subject attachment on server");
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task OnSubjectDetachedAsync(IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        var registeredSubject = subject.TryGetRegisteredSubject();
        if (registeredSubject is null || _nodeManager is null)
        {
            return;
        }

        _logger.LogDebug(
            "Server: Subject detached - {SubjectType}. Removing OPC UA node tracking...",
            subject.GetType().Name);

        // Remove node tracking (actual nodes remain in address space until restart per OPC UA SDK limitation)
        _nodeManager.RemoveSubjectNodes(subject);

        // Fire ModelChangeEvent to notify connected clients
        if (_configuration.EnableLiveSync && _configuration.EnableRemoteNodeManagement)
        {
            await FireModelChangeEventAsync(ModelChangeStructureVerbMask.NodeDeleted, cancellationToken).ConfigureAwait(false);
        }

        // Clean up mappings
        if (_subjectToNodeId.TryGetValue(subject, out var nodeId))
        {
            _nodeIdToSubject.Remove(nodeId);
            _subjectToNodeId.Remove(subject);
        }
    }

    public async Task OnRemoteNodeAddedAsync(ReferenceDescription node, NodeId parentNodeId, CancellationToken cancellationToken)
    {
        _logger.LogDebug(
            "Server: Remote node add requested - {NodeId}. Creating local subject...",
            node.NodeId);

        // TODO Phase 4: Implement handling of AddNodes requests from external clients
        // This will:
        // 1. Find parent subject using parentNodeId
        // 2. Create appropriate local subject (using TypeResolver or DynamicSubject)
        // 3. Attach to parent collection/property
        // 4. Create corresponding OPC UA node via CustomNodeManager

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task OnRemoteNodeRemovedAsync(NodeId nodeId, CancellationToken cancellationToken)
    {
        _logger.LogDebug(
            "Server: Remote node delete requested - {NodeId}. Removing local subject...",
            nodeId);

        // Find and detach local subject
        if (_nodeIdToSubject.TryGetValue(nodeId, out var subject))
        {
            // TODO Phase 4: Implement detachment from parent collection/property
            // This will integrate with LifecycleInterceptor

            _nodeIdToSubject.Remove(nodeId);
            _subjectToNodeId.Remove(subject);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public Task<ReferenceDescriptionCollection> BrowseNodeAsync(NodeId nodeId, CancellationToken cancellationToken)
    {
        // Server doesn't need to browse - it owns the address space
        // This method is here to satisfy the interface but is not used server-side
        return Task.FromResult(new ReferenceDescriptionCollection());
    }

    private async Task FireModelChangeEventAsync(ModelChangeStructureVerbMask verb, CancellationToken cancellationToken)
    {
        if (_server is null || _nodeManager is null)
        {
            return;
        }

        try
        {
            // Create a GeneralModelChangeEventState
            var eventState = new GeneralModelChangeEventState(null);
            
            // Set standard event properties
            var context = _server.DefaultSystemContext;
            eventState.Initialize(context, null, EventSeverity.Low, new LocalizedText("Address space structure changed"));
            
            // Set the source (Server object)
            eventState.SetChildValue(context, Opc.Ua.BrowseNames.SourceNode, ObjectIds.Server, false);
            eventState.SetChildValue(context, Opc.Ua.BrowseNames.SourceName, "Server", false);
            
            // Set event-specific properties
            eventState.SetChildValue(context, Opc.Ua.BrowseNames.Message, 
                new LocalizedText($"Address space structure changed: {verb}"), false);
            
            // Create the Changes array - in a real implementation, this would contain the actual affected NodeIds
            // For now, we'll create a minimal change structure
            var changes = new ModelChangeStructureDataType[]
            {
                new ModelChangeStructureDataType
                {
                    Verb = (byte)verb,
                    Affected = ObjectIds.Server, // Would be the actual affected node
                    AffectedType = ObjectTypeIds.BaseObjectType
                }
            };
            
            eventState.SetChildValue(context, Opc.Ua.BrowseNames.Changes, changes, false);
            
            // Report the event to the server
            eventState.SetChildValue(context, Opc.Ua.BrowseNames.Time, DateTime.UtcNow, false);
            eventState.SetChildValue(context, Opc.Ua.BrowseNames.ReceiveTime, DateTime.UtcNow, false);
            
            _server.ReportEvent(eventState);
            
            _logger.LogDebug("Fired ModelChangeEvent with verb: {Verb}", verb);

            await Task.CompletedTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fire ModelChangeEvent.");
        }
    }
}
