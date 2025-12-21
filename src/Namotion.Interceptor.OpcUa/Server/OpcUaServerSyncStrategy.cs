using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Dynamic;
using Namotion.Interceptor.OpcUa.Sync;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Opc.Ua;
using Opc.Ua.Server;

namespace Namotion.Interceptor.OpcUa.Server;

/// <summary>
/// Server-side implementation of OPC UA address space synchronization.
/// Handles creating OPC UA nodes for local subjects and firing ModelChangeEvents for remote clients.
/// </summary>
internal class OpcUaServerSyncStrategy : OpcUaSyncStrategyBase
{
    private readonly OpcUaServerConfiguration _serverConfiguration;
    private readonly OpcUaSubjectServerBackgroundService _serverService;

    private CustomNodeManager? _nodeManager;
    private IServerInternal? _server;

    public OpcUaServerSyncStrategy(
        OpcUaServerConfiguration configuration,
        OpcUaSubjectServerBackgroundService serverService,
        ILogger logger)
        : base(configuration, logger)
    {
        _serverConfiguration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _serverService = serverService ?? throw new ArgumentNullException(nameof(serverService));
    }

    /// <inheritdoc />
    protected override object DetachmentSource => _serverService;

    /// <summary>
    /// Sets the node manager and server. Must be called before any sync operations.
    /// </summary>
    public void SetNodeManager(CustomNodeManager? nodeManager, IServerInternal? server)
    {
        _nodeManager = nodeManager;
        _server = server;
    }

    public override async Task OnSubjectAttachedAsync(SubjectLifecycleChange change, CancellationToken cancellationToken)
    {
        var subject = change.Subject;
        var registeredSubject = subject.TryGetRegisteredSubject();
        if (registeredSubject is null || _nodeManager is null || _server is null)
        {
            return;
        }

        Logger.LogDebug(
            "Server: Subject attached - {SubjectType}. Creating OPC UA nodes...",
            subject.GetType().Name);

        try
        {
            // Create OPC UA nodes for this subject dynamically
            var createdNode = _nodeManager.CreateDynamicSubjectNodes(subject);

            if (createdNode is not null)
            {
                // Register the mapping
                RegisterMapping(subject, createdNode.NodeId);

                Logger.LogInformation(
                    "Dynamically created OPC UA nodes for subject {SubjectType} at NodeId {NodeId}",
                    subject.GetType().Name,
                    createdNode.NodeId);

                // Fire ModelChangeEvent to notify connected clients
                if (_serverConfiguration.EnableStructureSynchronization && _serverConfiguration.AllowRemoteNodeManagement)
                {
                    await FireModelChangeEventAsync(ModelChangeStructureVerbMask.NodeAdded, createdNode.NodeId).ConfigureAwait(false);
                }
            }
            else
            {
                Logger.LogDebug(
                    "Subject {SubjectType} already has nodes in address space",
                    subject.GetType().Name);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to create dynamic nodes for subject {SubjectType}", subject.GetType().Name);
        }
    }

    public override async Task OnSubjectDetachedAsync(SubjectLifecycleChange change, CancellationToken cancellationToken)
    {
        var subject = change.Subject;
        var registeredSubject = subject.TryGetRegisteredSubject();
        if (registeredSubject is null || _nodeManager is null)
        {
            return;
        }

        Logger.LogDebug(
            "Server: Subject detached - {SubjectType}. Removing OPC UA nodes...",
            subject.GetType().Name);

        // Get the NodeId before we clean up mappings
        var nodeId = FindNodeId(subject);

        try
        {
            // Remove nodes dynamically at runtime
            var removed = _nodeManager.RemoveDynamicSubjectNodes(subject);

            if (removed)
            {
                Logger.LogInformation(
                    "Dynamically removed OPC UA nodes for subject {SubjectType}",
                    subject.GetType().Name);

                // Fire ModelChangeEvent to notify connected clients
                if (_serverConfiguration.EnableStructureSynchronization && _serverConfiguration.AllowRemoteNodeManagement && nodeId is not null)
                {
                    await FireModelChangeEventAsync(ModelChangeStructureVerbMask.NodeDeleted, nodeId).ConfigureAwait(false);
                }
            }
            else
            {
                Logger.LogDebug(
                    "No nodes found to remove for subject {SubjectType}",
                    subject.GetType().Name);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to remove dynamic nodes for subject {SubjectType}", subject.GetType().Name);
        }

        // Clean up mappings (via base class)
        UnregisterMapping(subject);
    }

    public override Task OnRemoteNodeAddedAsync(ReferenceDescription node, NodeId parentNodeId, CancellationToken cancellationToken)
    {
        if (_nodeManager is null || _server is null)
        {
            return Task.CompletedTask;
        }

        Logger.LogDebug(
            "Server: Remote node add requested - {NodeId}. Creating local subject...",
            node.NodeId);

        try
        {
            // 1. Find parent subject using parentNodeId
            var parentSubject = FindSubject(parentNodeId);
            if (parentSubject is null)
            {
                Logger.LogWarning(
                    "Parent subject not found for external AddNodes request. ParentNodeId: {ParentNodeId}",
                    parentNodeId);
                return Task.CompletedTask;
            }

            var parentRegisteredSubject = parentSubject.TryGetRegisteredSubject();
            if (parentRegisteredSubject is null)
            {
                return Task.CompletedTask;
            }

            // 2. Find the property this node should map to
            var property = FindPropertyForNode(parentRegisteredSubject, node);
            if (property is null)
            {
                Logger.LogDebug(
                    "No matching property found for external node {NodeId} in subject {SubjectType}",
                    node.NodeId,
                    parentSubject.GetType().Name);
                return Task.CompletedTask;
            }

            // 3. Create local subject
            IInterceptorSubject? newSubject = null;
            var context = parentSubject.Context;

            if (property.IsSubjectReference)
            {
                // Try to create based on property type
                var propertyType = property.Type;
                if (propertyType.IsAssignableTo(typeof(IInterceptorSubject)) && !propertyType.IsAbstract && !propertyType.IsInterface)
                {
                    try
                    {
                        newSubject = DefaultSubjectFactory.Instance.CreateSubject(propertyType, context.TryGetService<IServiceProvider>());
                        newSubject.Context.AddFallbackContext(context);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(ex, "Failed to create subject of type {Type}, falling back to DynamicSubject", propertyType.Name);
                    }
                }

                // Fallback to DynamicSubject for unknown or abstract types
                newSubject ??= new DynamicSubject(context);
            }

            if (newSubject is not null)
            {
                var nodeId = ExpandedNodeId.ToNodeId(node.NodeId, _server.NamespaceUris);

                // Register mapping
                RegisterMapping(newSubject, nodeId);

                // Attach to parent property (this will trigger LifecycleInterceptor → create OPC UA nodes)
                property.SetValueFromSource(_serverService, null, newSubject);

                Logger.LogInformation(
                    "Created local subject {SubjectType} for external node {NodeId}",
                    newSubject.GetType().Name,
                    node.NodeId);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to create local subject for external node {NodeId}", node.NodeId);
        }

        return Task.CompletedTask;
    }

    private Task FireModelChangeEventAsync(ModelChangeStructureVerbMask verb, NodeId affectedNodeId)
    {
        if (_server is null || _nodeManager is null)
        {
            return Task.CompletedTask;
        }

        try
        {
            // Create a GeneralModelChangeEventState
            var eventState = new GeneralModelChangeEventState(null);

            // Set standard event properties
            var context = _server.DefaultSystemContext;
            eventState.Initialize(context, null, EventSeverity.Low, new LocalizedText("Address space structure changed"));

            // Set the source (Server object)
            eventState.SetChildValue(context, BrowseNames.SourceNode, ObjectIds.Server, false);
            eventState.SetChildValue(context, BrowseNames.SourceName, "Server", false);

            // Set event-specific properties
            eventState.SetChildValue(context, BrowseNames.Message,
                new LocalizedText($"Address space structure changed: {verb}"), false);

            // Create the Changes array with the actual affected NodeId
            var changes = new ModelChangeStructureDataType[]
            {
                new()
                {
                    Verb = (byte)verb,
                    Affected = affectedNodeId,
                    AffectedType = ObjectTypeIds.BaseObjectType
                }
            };

            eventState.SetChildValue(context, BrowseNames.Changes, changes, false);

            // Report the event to the server
            eventState.SetChildValue(context, BrowseNames.Time, DateTime.UtcNow, false);
            eventState.SetChildValue(context, BrowseNames.ReceiveTime, DateTime.UtcNow, false);

            _server.ReportEvent(eventState);

            Logger.LogDebug("Fired ModelChangeEvent with verb: {Verb} for node: {NodeId}", verb, affectedNodeId);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to fire ModelChangeEvent.");
        }

        return Task.CompletedTask;
    }
}
