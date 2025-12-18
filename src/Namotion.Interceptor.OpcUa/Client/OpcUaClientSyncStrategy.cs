using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Dynamic;
using Namotion.Interceptor.OpcUa.Sync;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Sources;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Client-side implementation of OPC UA address space synchronization.
/// Handles creating monitored items for value sync and optionally calling AddNodes/DeleteNodes on the server.
/// </summary>
internal class OpcUaClientSyncStrategy : IOpcUaSyncStrategy
{
    private readonly OpcUaClientConfiguration _configuration;
    private readonly OpcUaSubjectClientSource _clientSource;
    private readonly ILogger _logger;
    private readonly Dictionary<NodeId, IInterceptorSubject> _nodeIdToSubject = new();
    private readonly Dictionary<IInterceptorSubject, NodeId> _subjectToNodeId = new();
    private readonly Dictionary<IInterceptorSubject, List<MonitoredItem>> _subjectMonitoredItems = new();

    private Session? _session;

    public OpcUaClientSyncStrategy(
        OpcUaClientConfiguration configuration,
        OpcUaSubjectClientSource clientSource,
        ILogger logger)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _clientSource = clientSource ?? throw new ArgumentNullException(nameof(clientSource));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Sets the current OPC UA session. Must be called before any sync operations.
    /// </summary>
    public void SetSession(Session? session)
    {
        _session = session;
    }

    public async Task OnSubjectAttachedAsync(IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        var registeredSubject = subject.TryGetRegisteredSubject();
        if (registeredSubject is null || _session is null)
        {
            return;
        }

        _logger.LogDebug(
            "Client: Subject attached - {SubjectType}. Creating monitored items...",
            subject.GetType().Name);

        // Create monitored items for this subject's properties
        var monitoredItems = await CreateMonitoredItemsForSubjectAsync(registeredSubject, _session, cancellationToken).ConfigureAwait(false);
        if (monitoredItems.Count > 0)
        {
            _subjectMonitoredItems[subject] = monitoredItems;
            _logger.LogDebug(
                "Created {Count} monitored items for subject {SubjectType}",
                monitoredItems.Count,
                subject.GetType().Name);
        }

        // Try to create node on server if enabled and supported
        if (_configuration.EnableRemoteNodeManagement)
        {
            await TryCreateRemoteNodeAsync(subject, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task OnSubjectDetachedAsync(IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        var registeredSubject = subject.TryGetRegisteredSubject();
        if (registeredSubject is null || _session is null)
        {
            return;
        }

        _logger.LogDebug(
            "Client: Subject detached - {SubjectType}. Removing monitored items...",
            subject.GetType().Name);

        // Remove monitored items for this subject
        if (_subjectMonitoredItems.TryGetValue(subject, out var monitoredItems))
        {
            await RemoveMonitoredItemsAsync(monitoredItems, _session, cancellationToken).ConfigureAwait(false);
            _subjectMonitoredItems.Remove(subject);
            
            _logger.LogDebug(
                "Removed {Count} monitored items for subject {SubjectType}",
                monitoredItems.Count,
                subject.GetType().Name);
        }

        // Try to delete node on server if enabled and supported
        if (_configuration.EnableRemoteNodeManagement)
        {
            await TryDeleteRemoteNodeAsync(subject, cancellationToken).ConfigureAwait(false);
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
        if (_session is null)
        {
            return;
        }

        _logger.LogDebug(
            "Client: Remote node added - {NodeId}. Creating local subject...",
            node.NodeId);

        try
        {
            // 1. Find parent subject using parentNodeId
            if (!_nodeIdToSubject.TryGetValue(parentNodeId, out var parentSubject))
            {
                _logger.LogWarning(
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
                _logger.LogDebug(
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
                var newSubject = await _configuration.SubjectFactory.CreateSubjectAsync(property, node, _session, cancellationToken).ConfigureAwait(false);
                
                if (newSubject is not null)
                {
                    // Track the mapping
                    _nodeIdToSubject[nodeId] = newSubject;
                    _subjectToNodeId[newSubject] = nodeId;

                    // 4. Attach to parent property  
                    property.SetValueFromSource(_clientSource, null, newSubject);

                    _logger.LogInformation(
                        "Created subject {SubjectType} for remote node {NodeId}",
                        newSubject.GetType().Name,
                        node.NodeId);

                    // 5. Create monitored items for value sync
                    var registeredSubject = newSubject.TryGetRegisteredSubject();
                    if (registeredSubject is not null)
                    {
                        var monitoredItems = await CreateMonitoredItemsForSubjectAsync(registeredSubject, _session, cancellationToken).ConfigureAwait(false);
                        if (monitoredItems.Count > 0)
                        {
                            _subjectMonitoredItems[newSubject] = monitoredItems;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create local subject for remote node {NodeId}", node.NodeId);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private RegisteredSubjectProperty? FindPropertyForNode(RegisteredSubject registeredSubject, ReferenceDescription node)
    {
        var nodeIdString = node.NodeId.Identifier.ToString();
        var nodeNamespaceUri = node.NodeId.NamespaceUri ?? _session?.NamespaceUris.GetString(node.NodeId.NamespaceIndex);

        // Try to find by OpcUaNode attribute
        foreach (var property in registeredSubject.Properties)
        {
            var opcUaNodeAttribute = property.TryGetOpcUaNodeAttribute();
            if (opcUaNodeAttribute is not null && opcUaNodeAttribute.NodeIdentifier == nodeIdString)
            {
                var propertyNodeNamespaceUri = opcUaNodeAttribute.NodeNamespaceUri ?? _configuration.DefaultNamespaceUri;
                if (propertyNodeNamespaceUri == nodeNamespaceUri)
                {
                    return property;
                }
            }
        }

        // Try to find by path
        return _configuration.PathProvider.TryGetPropertyFromSegment(registeredSubject, node.BrowseName.Name);
    }

    public async Task OnRemoteNodeRemovedAsync(NodeId nodeId, CancellationToken cancellationToken)
    {
        if (_session is null)
        {
            return;
        }

        _logger.LogDebug(
            "Client: Remote node removed - {NodeId}. Removing local subject...",
            nodeId);

        // Find and detach local subject
        if (_nodeIdToSubject.TryGetValue(nodeId, out var subject))
        {
            try
            {
                // Remove monitored items first
                if (_subjectMonitoredItems.TryGetValue(subject, out var monitoredItems))
                {
                    await RemoveMonitoredItemsAsync(monitoredItems, _session, cancellationToken).ConfigureAwait(false);
                    _subjectMonitoredItems.Remove(subject);
                }

                // Detach from parent - just log for now as proper detachment requires registry navigation
                _logger.LogInformation(
                    "Removed subject {SubjectType} for deleted node {NodeId}",
                    subject.GetType().Name,
                    nodeId);

                _nodeIdToSubject.Remove(nodeId);
                _subjectToNodeId.Remove(subject);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to detach subject for removed node {NodeId}", nodeId);
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task<ReferenceDescriptionCollection> BrowseNodeAsync(NodeId nodeId, CancellationToken cancellationToken)
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

    private async Task<List<MonitoredItem>> CreateMonitoredItemsForSubjectAsync(
        RegisteredSubject registeredSubject,
        Session session,
        CancellationToken cancellationToken)
    {
        var monitoredItems = new List<MonitoredItem>();

        try
        {
            // Create monitored items for all properties that have OPC UA node mappings
            foreach (var property in registeredSubject.Properties)
            {
                if (property.Reference.TryGetPropertyData(_clientSource.OpcUaNodeIdKey, out var nodeIdData) && nodeIdData is NodeId nodeId)
                {
                    var opcUaNodeAttribute = property.TryGetOpcUaNodeAttribute();
                    var monitoredItem = new MonitoredItem
                    {
                        StartNodeId = nodeId,
                        AttributeId = Opc.Ua.Attributes.Value,
                        MonitoringMode = MonitoringMode.Reporting,
                        SamplingInterval = opcUaNodeAttribute?.SamplingInterval ?? _configuration.DefaultSamplingInterval,
                        QueueSize = opcUaNodeAttribute?.QueueSize ?? _configuration.DefaultQueueSize,
                        DiscardOldest = opcUaNodeAttribute?.DiscardOldest ?? _configuration.DefaultDiscardOldest,
                        Handle = property
                    };

                    monitoredItems.Add(monitoredItem);
                }
            }

            // If we have items to monitor, create a subscription and add them
            if (monitoredItems.Count > 0)
            {
                // Note: In a full implementation, this would integrate with SessionManager's SubscriptionManager
                // For now, we'll create a basic subscription
                var subscription = new Subscription(session.DefaultSubscription)
                {
                    PublishingEnabled = true,
                    PublishingInterval = _configuration.DefaultPublishingInterval,
                    DisableMonitoredItemCache = true
                };

                if (!session.AddSubscription(subscription))
                {
                    _logger.LogWarning("Failed to add subscription for dynamic subject");
                    return new List<MonitoredItem>();
                }

                await subscription.CreateAsync(cancellationToken).ConfigureAwait(false);

                foreach (var item in monitoredItems)
                {
                    subscription.AddItem(item);
                }

                await subscription.ApplyChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create monitored items for subject");
        }

        return monitoredItems;
    }

    private async Task RemoveMonitoredItemsAsync(
        List<MonitoredItem> monitoredItems,
        Session session,
        CancellationToken cancellationToken)
    {
        try
        {
            // Find subscriptions containing these items and remove them
            foreach (var subscription in session.Subscriptions.ToList())
            {
                var itemsToRemove = subscription.MonitoredItems
                    .Where(item => monitoredItems.Any(mi => mi.ClientHandle == item.ClientHandle))
                    .ToList();

                if (itemsToRemove.Count > 0)
                {
                    foreach (var item in itemsToRemove)
                    {
                        subscription.RemoveItem(item);
                    }

                    await subscription.ApplyChangesAsync(cancellationToken).ConfigureAwait(false);

                    // If subscription is now empty, remove it
                    if (subscription.MonitoredItemCount == 0)
                    {
                        session.RemoveSubscription(subscription);
                        subscription.Delete(true);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove monitored items");
        }
    }

    private async Task TryCreateRemoteNodeAsync(IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            // TODO Phase 4: Implement AddNodes service call
            // This will call _session.AddNodesAsync with proper node construction

            _logger.LogDebug(
                "Server does not support AddNodes or EnableRemoteNodeManagement is false. " +
                "Local subject '{SubjectType}' will sync values but not structure.",
                subject.GetType().Name);

            await Task.CompletedTask.ConfigureAwait(false);
        }
        catch (ServiceResultException ex) when (
            ex.StatusCode == StatusCodes.BadNotSupported ||
            ex.StatusCode == StatusCodes.BadServiceUnsupported)
        {
            _logger.LogWarning(
                "Server does not support AddNodes service. " +
                "Local subject '{SubjectType}' will sync values but not structure.",
                subject.GetType().Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create remote node for subject {SubjectType}.", subject.GetType().Name);
        }
    }

    private async Task TryDeleteRemoteNodeAsync(IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        if (_session is null || !_subjectToNodeId.TryGetValue(subject, out var nodeId))
        {
            return;
        }

        try
        {
            // TODO Phase 4: Implement DeleteNodes service call
            // This will call _session.DeleteNodesAsync

            _logger.LogDebug("Attempted to delete remote node {NodeId}.", nodeId);

            await Task.CompletedTask.ConfigureAwait(false);
        }
        catch (ServiceResultException ex) when (
            ex.StatusCode == StatusCodes.BadNotSupported ||
            ex.StatusCode == StatusCodes.BadServiceUnsupported)
        {
            _logger.LogWarning(
                "Server does not support DeleteNodes service for node {NodeId}.",
                nodeId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete remote node {NodeId}.", nodeId);
        }
    }
}
