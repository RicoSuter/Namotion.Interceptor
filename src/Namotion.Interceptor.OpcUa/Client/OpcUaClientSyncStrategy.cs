using Microsoft.Extensions.Logging;
using Namotion.Interceptor.OpcUa.Client.Connection;
using Namotion.Interceptor.OpcUa.Sync;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Client-side implementation of OPC UA address space synchronization.
/// Handles creating monitored items for value sync and optionally calling AddNodes/DeleteNodes on the server.
/// </summary>
internal class OpcUaClientSyncStrategy : OpcUaSyncStrategyBase
{
    private readonly OpcUaClientConfiguration _clientConfiguration;
    private readonly OpcUaSubjectClientSource _clientSource;

    private Session? _session;
    private SubscriptionManager? _subscriptionManager;

    public OpcUaClientSyncStrategy(
        OpcUaClientConfiguration configuration,
        OpcUaSubjectClientSource clientSource,
        ILogger logger)
        : base(configuration, logger)
    {
        _clientConfiguration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _clientSource = clientSource ?? throw new ArgumentNullException(nameof(clientSource));
    }

    /// <inheritdoc />
    protected override object DetachmentSource => _clientSource;

    /// <summary>
    /// Sets the current OPC UA session and subscription manager.
    /// Must be called before any sync operations.
    /// </summary>
    public void SetSession(Session? session, SubscriptionManager? subscriptionManager = null)
    {
        _session = session;
        _subscriptionManager = subscriptionManager;
    }

    /// <inheritdoc />
    protected override void OnBeforeSubjectRemoved(IInterceptorSubject subject)
    {
        // Remove monitored items before detaching
        _subscriptionManager?.RemoveItemsForSubject(subject);
    }

    public override async Task OnSubjectAttachedAsync(SubjectLifecycleChange change, CancellationToken cancellationToken)
    {
        var subject = change.Subject;
        var registeredSubject = subject.TryGetRegisteredSubject();
        if (registeredSubject is null || _session is null)
        {
            return;
        }

        Logger.LogDebug(
            "Client: Subject attached - {SubjectType}. Creating monitored items...",
            subject.GetType().Name);

        // Create monitored items using SubscriptionManager if available
        if (_subscriptionManager is not null)
        {
            await _subscriptionManager.AddMonitoredItemsForSubjectAsync(registeredSubject, _session, cancellationToken).ConfigureAwait(false);
            Logger.LogDebug(
                "Created monitored items via SubscriptionManager for subject {SubjectType}",
                subject.GetType().Name);
        }

        // Try to create node on server if enabled and supported
        if (_clientConfiguration.EnableRemoteNodeManagement)
        {
            await TryCreateRemoteNodeAsync(subject, cancellationToken).ConfigureAwait(false);
        }
    }

    public override Task OnSubjectDetachedAsync(SubjectLifecycleChange change, CancellationToken cancellationToken)
    {
        var subject = change.Subject;
        var registeredSubject = subject.TryGetRegisteredSubject();
        if (registeredSubject is null || _session is null)
        {
            return Task.CompletedTask;
        }

        Logger.LogDebug(
            "Client: Subject detached - {SubjectType}. Removing monitored items...",
            subject.GetType().Name);

        // Remove monitored items using SubscriptionManager if available
        if (_subscriptionManager is not null)
        {
            _subscriptionManager.RemoveItemsForSubject(subject);
            Logger.LogDebug(
                "Removed monitored items via SubscriptionManager for subject {SubjectType}",
                subject.GetType().Name);
        }

        // Try to delete node on server if enabled and supported
        if (_clientConfiguration.EnableRemoteNodeManagement)
        {
            return TryDeleteRemoteNodeAsync(subject, cancellationToken);
        }

        // Clean up mappings (via base class)
        UnregisterMapping(subject);
        return Task.CompletedTask;
    }

    public override async Task OnRemoteNodeAddedAsync(ReferenceDescription node, NodeId parentNodeId, CancellationToken cancellationToken)
    {
        if (_session is null)
        {
            return;
        }

        Logger.LogDebug(
            "Client: Remote node added - {NodeId}. Creating local subject...",
            node.NodeId);

        try
        {
            // 1. Find parent subject using parentNodeId
            var parentSubject = FindSubject(parentNodeId);
            if (parentSubject is null)
            {
                Logger.LogWarning(
                    "Parent subject not found for node {NodeId}. Parent: {ParentNodeId}",
                    node.NodeId,
                    parentNodeId);
                return;
            }

            var parentRegisteredSubject = parentSubject.TryGetRegisteredSubject();
            if (parentRegisteredSubject is null)
            {
                return;
            }

            // 2. Try to find the property this node should map to
            var property = FindPropertyForNode(parentRegisteredSubject, node);
            if (property is null)
            {
                Logger.LogDebug(
                    "No matching property found for remote node {NodeId} in subject {SubjectType}",
                    node.NodeId,
                    parentSubject.GetType().Name);
                return;
            }

            // 3. For object nodes, create a subject
            if (node.NodeClass == NodeClass.Object && property.IsSubjectReference)
            {
                // Create subject using SubjectFactory
                var nodeId = ExpandedNodeId.ToNodeId(node.NodeId, _session.NamespaceUris);
                var newSubject = await _clientConfiguration.SubjectFactory.CreateSubjectAsync(property, node, _session, cancellationToken).ConfigureAwait(false);

                if (newSubject is not null)
                {
                    // Track the mapping
                    RegisterMapping(newSubject, nodeId);

                    // 4. Attach to parent property
                    property.SetValueFromSource(_clientSource, null, newSubject);

                    Logger.LogInformation(
                        "Created subject {SubjectType} for remote node {NodeId}",
                        newSubject.GetType().Name,
                        node.NodeId);

                    // 5. Create monitored items for value sync using SubscriptionManager
                    var registeredSubject = newSubject.TryGetRegisteredSubject();
                    if (registeredSubject is not null && _subscriptionManager is not null)
                    {
                        await _subscriptionManager.AddMonitoredItemsForSubjectAsync(registeredSubject, _session, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to create local subject for remote node {NodeId}", node.NodeId);
        }
    }

    public override async Task<ReferenceDescriptionCollection> BrowseNodeAsync(NodeId nodeId, CancellationToken cancellationToken)
    {
        if (_session is null)
        {
            return new ReferenceDescriptionCollection();
        }

        const uint nodeClassMask = (uint)NodeClass.Variable | (uint)NodeClass.Object;

        var (_, _, nodeProperties, _) = await _session.BrowseAsync(
            requestHeader: null,
            view: null,
            [nodeId],
            maxResultsToReturn: 0u,
            BrowseDirection.Forward,
            ReferenceTypeIds.HierarchicalReferences,
            includeSubtypes: true,
            nodeClassMask,
            cancellationToken).ConfigureAwait(false);

        return nodeProperties[0];
    }

    private async Task TryCreateRemoteNodeAsync(IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        if (_session is null)
        {
            return;
        }

        var registeredSubject = subject.TryGetRegisteredSubject();
        if (registeredSubject is null)
        {
            return;
        }

        try
        {
            // Find parent node ID
            NodeId parentNodeId = ObjectIds.ObjectsFolder;
            if (registeredSubject.Parents.Length > 0)
            {
                var parent = registeredSubject.Parents[0];
                var parentNodeIdValue = FindNodeId(parent.Property.Parent.Subject);
                if (parentNodeIdValue is not null)
                {
                    parentNodeId = parentNodeIdValue;
                }
            }

            // Build AddNodesItem
            var browseName = GetBrowseNameForSubject(subject);
            var namespaceIndex = GetNamespaceIndex();
            var nodeId = new NodeId(Guid.NewGuid().ToString(), namespaceIndex);

            var addNodesItem = new AddNodesItem
            {
                ParentNodeId = new ExpandedNodeId(parentNodeId),
                ReferenceTypeId = ReferenceTypeIds.HasComponent,
                RequestedNewNodeId = new ExpandedNodeId(nodeId),
                BrowseName = browseName,
                NodeClass = NodeClass.Object,
                NodeAttributes = new ExtensionObject(new ObjectAttributes
                {
                    DisplayName = new LocalizedText(browseName.Name),
                    Description = LocalizedText.Null,
                    WriteMask = 0,
                    UserWriteMask = 0,
                    EventNotifier = 0
                }),
                TypeDefinition = new ExpandedNodeId(ObjectTypeIds.BaseObjectType)
            };

            var response = await _session.AddNodesAsync(
                requestHeader: null,
                [addNodesItem],
                cancellationToken).ConfigureAwait(false);

            if (response.Results.Count > 0)
            {
                var result = response.Results[0];
                if (StatusCode.IsGood(result.StatusCode))
                {
                    var assignedNodeId = result.AddedNodeId;
                    RegisterMapping(subject, assignedNodeId);

                    Logger.LogInformation(
                        "Successfully created remote node {NodeId} for subject {SubjectType}",
                        assignedNodeId,
                        subject.GetType().Name);
                }
                else if (result.StatusCode == StatusCodes.BadNotSupported ||
                         result.StatusCode == StatusCodes.BadServiceUnsupported)
                {
                    Logger.LogWarning(
                        "Server does not support AddNodes service. " +
                        "Local subject '{SubjectType}' will sync values but not structure.",
                        subject.GetType().Name);
                }
                else
                {
                    Logger.LogWarning(
                        "Failed to create remote node for subject {SubjectType}: {StatusCode}",
                        subject.GetType().Name,
                        result.StatusCode);
                }
            }
        }
        catch (ServiceResultException ex) when (
            ex.StatusCode == StatusCodes.BadNotSupported ||
            ex.StatusCode == StatusCodes.BadServiceUnsupported)
        {
            Logger.LogWarning(
                "Server does not support AddNodes service. " +
                "Local subject '{SubjectType}' will sync values but not structure.",
                subject.GetType().Name);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to create remote node for subject {SubjectType}.", subject.GetType().Name);
        }
    }

    private async Task TryDeleteRemoteNodeAsync(IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        if (_session is null)
        {
            return;
        }

        var nodeId = FindNodeId(subject);
        if (nodeId is null)
        {
            return;
        }

        try
        {
            var deleteNodesItem = new DeleteNodesItem
            {
                NodeId = nodeId,
                DeleteTargetReferences = true
            };

            var response = await _session.DeleteNodesAsync(
                requestHeader: null,
                [deleteNodesItem],
                cancellationToken).ConfigureAwait(false);

            if (response.Results.Count > 0)
            {
                var result = response.Results[0];
                if (StatusCode.IsGood(result))
                {
                    Logger.LogInformation(
                        "Successfully deleted remote node {NodeId} for subject {SubjectType}",
                        nodeId,
                        subject.GetType().Name);
                }
                else if (result == StatusCodes.BadNotSupported ||
                         result == StatusCodes.BadServiceUnsupported)
                {
                    Logger.LogWarning(
                        "Server does not support DeleteNodes service for node {NodeId}.",
                        nodeId);
                }
                else
                {
                    Logger.LogWarning(
                        "Failed to delete remote node {NodeId}: {StatusCode}",
                        nodeId,
                        result);
                }
            }
        }
        catch (ServiceResultException ex) when (
            ex.StatusCode == StatusCodes.BadNotSupported ||
            ex.StatusCode == StatusCodes.BadServiceUnsupported)
        {
            Logger.LogWarning(
                "Server does not support DeleteNodes service for node {NodeId}.",
                nodeId);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to delete remote node {NodeId}.", nodeId);
        }

        UnregisterMapping(subject);
    }

    private QualifiedName GetBrowseNameForSubject(IInterceptorSubject subject)
    {
        var registeredSubject = subject.TryGetRegisteredSubject();
        var name = registeredSubject?.Parents
            .Select(p => p.Property.Name)
            .FirstOrDefault()
            ?? subject.GetType().Name;

        var namespaceIndex = GetNamespaceIndex();
        return new QualifiedName(name, namespaceIndex);
    }

    private ushort GetNamespaceIndex()
    {
        var namespaceUri = _clientConfiguration.DefaultNamespaceUri ?? "http://namotion.com/Interceptor/";
        return (ushort)_session!.NamespaceUris.GetIndex(namespaceUri);
    }
}
