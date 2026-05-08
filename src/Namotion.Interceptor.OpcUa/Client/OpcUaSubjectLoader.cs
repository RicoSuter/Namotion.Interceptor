using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Client.Connection;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

internal class OpcUaSubjectLoader
{
    private const uint NodeClassMask = (uint)NodeClass.Variable | (uint)NodeClass.Object;

    private readonly OpcUaClientConfiguration _configuration;
    private readonly ILogger _logger;
    private readonly SourceOwnershipManager _ownership;
    private readonly OpcUaSubjectClientSource _source;

    public OpcUaSubjectLoader(
        OpcUaClientConfiguration configuration,
        SourceOwnershipManager ownership,
        OpcUaSubjectClientSource source,
        ILogger logger)
    {
        _configuration = configuration;
        _ownership = ownership;
        _source = source;
        _logger = logger;
    }

    public async Task<IReadOnlyList<MonitoredItem>> LoadSubjectAsync(
        IInterceptorSubject subject,
        ReferenceDescription node,
        ISession session,
        CancellationToken cancellationToken)
    {
        return await LoadSubjectAsync(subject, node, session, subjectMap: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MonitoredItem>> LoadSubjectAsync(
        IInterceptorSubject subject,
        ReferenceDescription node,
        ISession session,
        ConnectorSubjectMap<NodeId>? subjectMap,
        CancellationToken cancellationToken)
    {
        var monitoredItems = new List<MonitoredItem>();
        var loadedSubjects = new HashSet<IInterceptorSubject>();
        var subjectsByNodeId = new Dictionary<NodeId, IInterceptorSubject>();
        await LoadSubjectAsync(subject, node, session, monitoredItems, loadedSubjects, subjectsByNodeId, subjectMap, cancellationToken).ConfigureAwait(false);
        return monitoredItems;
    }

    /// <summary>
    /// Re-browses a parent node's children, compares with local subject state, and creates/removes
    /// subjects and monitored items to reconcile the local model with the remote OPC UA address space.
    /// </summary>
    /// <param name="parentSubject">The local parent subject to reconcile.</param>
    /// <param name="parentNodeId">The OPC UA NodeId of the parent node to re-browse.</param>
    /// <param name="session">The active OPC UA session.</param>
    /// <param name="subjectMap">The map from NodeId to subject for dedup and cleanup.</param>
    /// <param name="subscriptionManager">The subscription manager for adding/removing monitored items.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The list of new monitored items that were created during reconciliation.</returns>
    public async Task<IReadOnlyList<MonitoredItem>> ReconcileSubtreeAsync(
        IInterceptorSubject parentSubject,
        NodeId parentNodeId,
        ISession session,
        ConnectorSubjectMap<NodeId> subjectMap,
        SubscriptionManager subscriptionManager,
        CancellationToken cancellationToken)
    {
        var newMonitoredItems = new List<MonitoredItem>();
        var visitedSubjects = new HashSet<IInterceptorSubject>();
        await ReconcileSubtreeInternalAsync(
            parentSubject, parentNodeId, session, subjectMap,
            newMonitoredItems, visitedSubjects, cancellationToken).ConfigureAwait(false);

        // Register any new monitored items with existing subscriptions
        if (newMonitoredItems.Count > 0)
        {
            await subscriptionManager.AddMonitoredItemsAsync(
                newMonitoredItems, (Session)session, cancellationToken).ConfigureAwait(false);
        }

        return newMonitoredItems;
    }

    private async Task ReconcileSubtreeInternalAsync(
        IInterceptorSubject parentSubject,
        NodeId parentNodeId,
        ISession session,
        ConnectorSubjectMap<NodeId> subjectMap,
        List<MonitoredItem> newMonitoredItems,
        HashSet<IInterceptorSubject> visitedSubjects,
        CancellationToken cancellationToken)
    {
        if (!visitedSubjects.Add(parentSubject))
        {
            return;
        }

        var registeredSubject = parentSubject.TryGetRegisteredSubject();
        if (registeredSubject is null)
        {
            return;
        }

        // Browse the current children from the OPC UA server
        var remoteChildren = await BrowseNodeAsync(parentNodeId, session, cancellationToken).ConfigureAwait(false);

        _logger.LogDebug(
            "ReconcileSubtreeInternalAsync: parentNodeId={ParentNodeId}, remoteChildren={RemoteChildCount}, properties={PropertyCount}.",
            parentNodeId, remoteChildren.Count, registeredSubject.Properties.Length);

        foreach (var property in registeredSubject.Properties)
        {
            if (property.IsSubjectCollection)
            {
                await ReconcileCollectionPropertyAsync(
                    property, parentSubject, parentNodeId, remoteChildren, session, subjectMap,
                    newMonitoredItems, visitedSubjects, cancellationToken).ConfigureAwait(false);
            }
            else if (property.IsSubjectDictionary)
            {
                await ReconcileDictionaryPropertyAsync(
                    property, parentSubject, parentNodeId, remoteChildren, session, subjectMap,
                    newMonitoredItems, visitedSubjects, cancellationToken).ConfigureAwait(false);
            }
            else if (property.IsSubjectReference)
            {
                await ReconcileReferencePropertyAsync(
                    property, parentSubject, parentNodeId, remoteChildren, session, subjectMap,
                    newMonitoredItems, visitedSubjects, cancellationToken).ConfigureAwait(false);
            }
        }

        // Recurse into existing child subjects to handle nested structural changes
        foreach (var property in registeredSubject.Properties)
        {
            if (!property.CanContainSubjects)
            {
                continue;
            }

            foreach (var child in property.Children)
            {
                if (child.Subject is not null &&
                    child.Subject.TryGetData(OpcUaSubjectClientSource.SubjectNodeIdDataKey, out var childNodeIdObj) &&
                    childNodeIdObj is NodeId childNodeId)
                {
                    await ReconcileSubtreeInternalAsync(
                        child.Subject, childNodeId, session, subjectMap,
                        newMonitoredItems, visitedSubjects, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task ReconcileCollectionPropertyAsync(
        RegisteredSubjectProperty property,
        IInterceptorSubject parentSubject,
        NodeId parentNodeId,
        ReferenceDescriptionCollection remoteChildren,
        ISession session,
        ConnectorSubjectMap<NodeId> subjectMap,
        List<MonitoredItem> newMonitoredItems,
        HashSet<IInterceptorSubject> visitedSubjects,
        CancellationToken cancellationToken)
    {
        var propertyName = property.ResolvePropertyName(_configuration.NodeMapper);
        if (propertyName is null)
        {
            return;
        }

        // Find the container node (the folder) for this collection property
        ReferenceDescription? containerNode = null;
        foreach (var remote in remoteChildren)
        {
            if (remote.BrowseName.Name == propertyName)
            {
                containerNode = remote;
                break;
            }
        }

        if (containerNode is null)
        {
            return;
        }

        var containerNodeId = ExpandedNodeId.ToNodeId(containerNode.NodeId, session.NamespaceUris);
        var remoteChildNodes = await BrowseNodeAsync(containerNodeId, session, cancellationToken).ConfigureAwait(false);

        // Build a set of remote NodeIds
        var remoteNodeIds = new HashSet<NodeId>();
        foreach (var remoteChild in remoteChildNodes)
        {
            var remoteChildNodeId = ExpandedNodeId.ToNodeId(remoteChild.NodeId, session.NamespaceUris);
            if (remoteChildNodeId is not null)
            {
                remoteNodeIds.Add(remoteChildNodeId);
            }
        }

        // Build a set of local subject NodeIds
        var localChildren = property.Children;
        var localNodeIds = new Dictionary<NodeId, IInterceptorSubject>();
        foreach (var child in localChildren)
        {
            if (child.Subject is not null &&
                child.Subject.TryGetData(OpcUaSubjectClientSource.SubjectNodeIdDataKey, out var nodeIdObj) &&
                nodeIdObj is NodeId nodeId)
            {
                localNodeIds[nodeId] = child.Subject;
            }
        }

        // Detect additions: remote nodes not present locally
        var hasChanges = false;
        var currentSubjects = new List<IInterceptorSubject>();
        foreach (var child in localChildren)
        {
            if (child.Subject is not null)
            {
                currentSubjects.Add(child.Subject);
            }
        }

        for (var i = 0; i < remoteChildNodes.Count; i++)
        {
            var remoteChild = remoteChildNodes[i];
            var remoteChildNodeId = ExpandedNodeId.ToNodeId(remoteChild.NodeId, session.NamespaceUris);
            if (remoteChildNodeId is not null && !localNodeIds.ContainsKey(remoteChildNodeId))
            {
                // New remote node: create subject
                if (subjectMap.TryGetSubject(remoteChildNodeId, out var existingSubject) && existingSubject is not null)
                {
                    currentSubjects.Add(existingSubject);
                }
                else
                {
                    var newSubject = await _configuration.SubjectFactory.CreateCollectionSubjectAsync(
                        property, remoteChild, currentSubjects.Count, session, cancellationToken).ConfigureAwait(false);
                    newSubject.Context.AddFallbackContext(parentSubject.Context);

                    // Load the new subject's properties and set up monitored items
                    var loadedSubjects = new HashSet<IInterceptorSubject>();
                    var subjectsByNodeId = new Dictionary<NodeId, IInterceptorSubject>();
                    await LoadSubjectAsync(
                        newSubject, remoteChild, session, newMonitoredItems, loadedSubjects,
                        subjectsByNodeId, subjectMap, cancellationToken).ConfigureAwait(false);

                    currentSubjects.Add(newSubject);
                }

                hasChanges = true;

                _logger.LogInformation(
                    "Reconciler: added new collection item for property {PropertyName} (NodeId: {NodeId}).",
                    propertyName, remoteChildNodeId);
            }
        }

        // Detect removals: local subjects not present remotely
        var survivingSubjects = new List<IInterceptorSubject>();
        foreach (var subject in currentSubjects)
        {
            if (subject.TryGetData(OpcUaSubjectClientSource.SubjectNodeIdDataKey, out var nodeIdObj) &&
                nodeIdObj is NodeId nodeId && !remoteNodeIds.Contains(nodeId))
            {
                // Removed remotely: skip this subject (it will be detached when the collection is set)
                hasChanges = true;
                subjectMap.Remove(nodeId);

                _logger.LogInformation(
                    "Reconciler: removed collection item for property {PropertyName} (NodeId: {NodeId}).",
                    propertyName, nodeId);
            }
            else
            {
                survivingSubjects.Add(subject);
            }
        }

        if (hasChanges)
        {
            var collection = DefaultSubjectFactory.Instance
                .CreateSubjectCollection(property.Type, survivingSubjects);
            property.SetValueFromSource(_source, null, null, collection);
        }
    }

    private async Task ReconcileDictionaryPropertyAsync(
        RegisteredSubjectProperty property,
        IInterceptorSubject parentSubject,
        NodeId parentNodeId,
        ReferenceDescriptionCollection remoteChildren,
        ISession session,
        ConnectorSubjectMap<NodeId> subjectMap,
        List<MonitoredItem> newMonitoredItems,
        HashSet<IInterceptorSubject> visitedSubjects,
        CancellationToken cancellationToken)
    {
        var propertyName = property.ResolvePropertyName(_configuration.NodeMapper);
        if (propertyName is null)
        {
            return;
        }

        // Find the container node for this dictionary property
        ReferenceDescription? containerNode = null;
        foreach (var remote in remoteChildren)
        {
            if (remote.BrowseName.Name == propertyName)
            {
                containerNode = remote;
                break;
            }
        }

        if (containerNode is null)
        {
            return;
        }

        var containerNodeId = ExpandedNodeId.ToNodeId(containerNode.NodeId, session.NamespaceUris);
        var remoteChildNodes = await BrowseNodeAsync(containerNodeId, session, cancellationToken).ConfigureAwait(false);

        // Build a set of remote keys (BrowseName)
        var remoteKeys = new Dictionary<string, ReferenceDescription>();
        foreach (var remoteChild in remoteChildNodes)
        {
            remoteKeys[remoteChild.BrowseName.Name] = remoteChild;
        }

        // Build local key->subject map
        var localKeys = new Dictionary<string, IInterceptorSubject>();
        foreach (var child in property.Children)
        {
            if (child.Index is string key && child.Subject is not null)
            {
                localKeys[key] = child.Subject;
            }
        }

        var hasChanges = false;
        var currentEntries = new Dictionary<object, IInterceptorSubject>(localKeys.Count);
        foreach (var kvp in localKeys)
        {
            currentEntries[kvp.Key] = kvp.Value;
        }

        // Detect additions: remote keys not present locally
        foreach (var remoteEntry in remoteKeys)
        {
            if (!localKeys.ContainsKey(remoteEntry.Key))
            {
                var remoteChild = remoteEntry.Value;
                var remoteChildNodeId = ExpandedNodeId.ToNodeId(remoteChild.NodeId, session.NamespaceUris);

                IInterceptorSubject newSubject;
                if (remoteChildNodeId is not null &&
                    subjectMap.TryGetSubject(remoteChildNodeId, out var existingSubject) &&
                    existingSubject is not null)
                {
                    newSubject = existingSubject;
                }
                else
                {
                    newSubject = await _configuration.SubjectFactory.CreateCollectionSubjectAsync(
                        property, remoteChild, remoteEntry.Key, session, cancellationToken).ConfigureAwait(false);
                    newSubject.Context.AddFallbackContext(parentSubject.Context);

                    var loadedSubjects = new HashSet<IInterceptorSubject>();
                    var subjectsByNodeId = new Dictionary<NodeId, IInterceptorSubject>();
                    await LoadSubjectAsync(
                        newSubject, remoteChild, session, newMonitoredItems, loadedSubjects,
                        subjectsByNodeId, subjectMap, cancellationToken).ConfigureAwait(false);
                }

                currentEntries[remoteEntry.Key] = newSubject;
                hasChanges = true;

                _logger.LogInformation(
                    "Reconciler: added new dictionary entry '{Key}' for property {PropertyName}.",
                    remoteEntry.Key, propertyName);
            }
        }

        // Detect removals: local keys not present remotely
        var keysToRemove = new List<string>();
        foreach (var localEntry in localKeys)
        {
            if (!remoteKeys.ContainsKey(localEntry.Key))
            {
                keysToRemove.Add(localEntry.Key);
                hasChanges = true;

                if (localEntry.Value.TryGetData(OpcUaSubjectClientSource.SubjectNodeIdDataKey, out var nodeIdObj) &&
                    nodeIdObj is NodeId nodeId)
                {
                    subjectMap.Remove(nodeId);
                }

                _logger.LogInformation(
                    "Reconciler: removed dictionary entry '{Key}' for property {PropertyName}.",
                    localEntry.Key, propertyName);
            }
        }

        foreach (var key in keysToRemove)
        {
            currentEntries.Remove(key);
        }

        if (hasChanges)
        {
            var dictionary = DefaultSubjectFactory.Instance.CreateSubjectDictionary(property.Type, currentEntries);
            property.SetValueFromSource(_source, null, null, dictionary);
        }
    }

    private async Task ReconcileReferencePropertyAsync(
        RegisteredSubjectProperty property,
        IInterceptorSubject parentSubject,
        NodeId parentNodeId,
        ReferenceDescriptionCollection remoteChildren,
        ISession session,
        ConnectorSubjectMap<NodeId> subjectMap,
        List<MonitoredItem> newMonitoredItems,
        HashSet<IInterceptorSubject> visitedSubjects,
        CancellationToken cancellationToken)
    {
        // Check if this reference is treated as a VariableNode; if so, skip reconciliation
        var nodeConfiguration = _configuration.NodeMapper.TryGetNodeConfiguration(property);
        if (nodeConfiguration?.NodeClass == Mapping.OpcUaNodeClass.Variable)
        {
            return;
        }

        var propertyName = property.ResolvePropertyName(_configuration.NodeMapper);
        if (propertyName is null)
        {
            return;
        }

        // Find the remote node for this reference property
        ReferenceDescription? remoteNode = null;
        foreach (var remote in remoteChildren)
        {
            if (remote.BrowseName.Name == propertyName)
            {
                remoteNode = remote;
                break;
            }
        }

        var existingChild = property.Children.SingleOrDefault();
        var existingSubject = existingChild.Subject;

        // Determine if the existing local subject matches the remote node
        var needsReplacement = false;
        if (remoteNode is not null && existingSubject is not null)
        {
            var remoteNodeId = ExpandedNodeId.ToNodeId(remoteNode.NodeId, session.NamespaceUris);
            if (existingSubject.TryGetData(OpcUaSubjectClientSource.SubjectNodeIdDataKey, out var existingNodeIdObj) &&
                existingNodeIdObj is NodeId existingNodeId)
            {
                // Both exist but NodeIds differ: the reference was replaced on the server
                if (remoteNodeId is not null && !remoteNodeId.Equals(existingNodeId))
                {
                    needsReplacement = true;
                    subjectMap.Remove(existingNodeId);

                    _logger.LogInformation(
                        "Reconciler: reference for property {PropertyName} changed from {OldNodeId} to {NewNodeId}.",
                        propertyName, existingNodeId, remoteNodeId);
                }
            }
            else
            {
                // Local subject has no NodeId, treat as replacement
                needsReplacement = true;
                _logger.LogDebug(
                    "Reconciler: reference for property {PropertyName} has no local NodeId, treating as replacement.",
                    propertyName);
            }
        }

        if (remoteNode is not null && (existingSubject is null || needsReplacement))
        {
            // New or replaced remote reference: create subject
            var remoteNodeId = ExpandedNodeId.ToNodeId(remoteNode.NodeId, session.NamespaceUris);

            IInterceptorSubject newSubject;
            if (remoteNodeId is not null &&
                subjectMap.TryGetSubject(remoteNodeId, out var reusedSubject) &&
                reusedSubject is not null)
            {
                newSubject = reusedSubject;
            }
            else
            {
                newSubject = await _configuration.SubjectFactory.CreateSubjectAsync(
                    property, remoteNode, session, cancellationToken).ConfigureAwait(false);
                newSubject.Context.AddFallbackContext(parentSubject.Context);

                var loadedSubjects = new HashSet<IInterceptorSubject>();
                var subjectsByNodeId = new Dictionary<NodeId, IInterceptorSubject>();
                await LoadSubjectAsync(
                    newSubject, remoteNode, session, newMonitoredItems, loadedSubjects,
                    subjectsByNodeId, subjectMap, cancellationToken).ConfigureAwait(false);
            }

            property.SetValueFromSource(_source, null, null, newSubject);

            _logger.LogInformation(
                "Reconciler: set reference for property {PropertyName} (NodeId: {NodeId}).",
                propertyName, remoteNode.NodeId);
        }
        else if (remoteNode is null && existingSubject is not null)
        {
            // Remote reference gone: detach
            if (existingSubject.TryGetData(OpcUaSubjectClientSource.SubjectNodeIdDataKey, out var nodeIdObj) &&
                nodeIdObj is NodeId nodeId)
            {
                subjectMap.Remove(nodeId);
            }

            property.SetValueFromSource(_source, null, null, null);

            _logger.LogInformation(
                "Reconciler: cleared reference for property {PropertyName}.",
                propertyName);
        }
    }

    private async Task LoadSubjectAsync(IInterceptorSubject subject,
        ReferenceDescription node,
        ISession session,
        List<MonitoredItem> monitoredItems,
        HashSet<IInterceptorSubject> loadedSubjects,
        Dictionary<NodeId, IInterceptorSubject> subjectsByNodeId,
        ConnectorSubjectMap<NodeId>? subjectMap,
        CancellationToken cancellationToken)
    {
        if (!loadedSubjects.Add(subject))
        {
            return;
        }

        var registeredSubject = subject.TryGetRegisteredSubject();
        if (registeredSubject is null)
        {
            return;
        }

        var nodeId = ExpandedNodeId.ToNodeId(node.NodeId, session.NamespaceUris);

        // Store NodeId in subject data for later reconciliation and external access
        if (nodeId is not null)
        {
            subject.SetData(OpcUaSubjectClientSource.SubjectNodeIdDataKey, nodeId);
            subjectMap?.Add(nodeId, subject);
        }

        if (nodeId is null)
        {
            return;
        }

        var nodeReferences = await BrowseNodeAsync(nodeId, session, cancellationToken).ConfigureAwait(false);
        
        for (var index = 0; index < nodeReferences.Count; index++)
        {
            var nodeReference = nodeReferences[index];
            var property = await FindSubjectPropertyAsync(registeredSubject, nodeReference, session, cancellationToken).ConfigureAwait(false);
            if (property is null)
            {
                var dynamicPropertyName = nodeReference.BrowseName.Name;

                var propertyExists = false;
                foreach (var childProperty in registeredSubject.Properties)
                {
                    if (childProperty.Name == dynamicPropertyName)
                    {
                        propertyExists = true;
                        break;
                    }
                }

                if (propertyExists)
                {
                    continue;
                }

                var addAsDynamic = _configuration.ShouldAddDynamicProperty is not null &&
                    await _configuration.ShouldAddDynamicProperty(nodeReference, cancellationToken).ConfigureAwait(false);

                if (!addAsDynamic)
                {
                    continue;
                }

                // Infer CLR type from OPC UA variable metadata if possible
                var inferredType = await _configuration.TypeResolver.TryGetTypeForNodeAsync(session, nodeReference, cancellationToken).ConfigureAwait(false);
                if (inferredType is null)
                {
                    _logger.LogWarning(
                        "Could not infer type for dynamic property '{PropertyName}' (NodeId: {NodeId}). Skipping property.",
                        dynamicPropertyName, nodeReference.NodeId);

                    continue;
                }

                object? value = null;
                property = registeredSubject.AddProperty(
                    dynamicPropertyName,
                    inferredType,
                    _ => value,
                    (_, o) => value = o,
                    _configuration.TypeResolver.GetDynamicPropertyAttributes(nodeReference, session));
            }

            var propertyName = property.ResolvePropertyName(_configuration.NodeMapper);
            if (propertyName is not null)
            {
                var childNodeId = ExpandedNodeId.ToNodeId(nodeReference.NodeId, session.NamespaceUris);

                if (property.IsSubjectReference)
                {
                    // Check if this should be treated as a VariableNode
                    var nodeConfiguration = _configuration.NodeMapper.TryGetNodeConfiguration(property);
                    if (nodeConfiguration?.NodeClass == Mapping.OpcUaNodeClass.Variable)
                    {
                        await LoadVariableNodeForSubjectAsync(property, childNodeId, session, monitoredItems, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        // Claim source ownership for structural properties when remote node
                        // management is enabled, so changes are captured by the ChangeQueueProcessor.
                        if (_configuration.EnableRemoteNodeManagement)
                        {
                            _ownership.ClaimSource(property.Reference);
                        }

                        await LoadSubjectReferenceAsync(property, nodeReference, subject, session, monitoredItems, loadedSubjects, subjectsByNodeId, subjectMap, cancellationToken).ConfigureAwait(false);
                    }
                }
                else if (property.IsSubjectCollection)
                {
                    // Claim source ownership for structural properties when remote node
                    // management is enabled, so changes are captured by the ChangeQueueProcessor.
                    if (_configuration.EnableRemoteNodeManagement)
                    {
                        _ownership.ClaimSource(property.Reference);
                    }

                    await LoadSubjectCollectionAsync(property, childNodeId, monitoredItems, session, loadedSubjects, subjectsByNodeId, subjectMap, cancellationToken).ConfigureAwait(false);
                }
                else if (property.IsSubjectDictionary)
                {
                    // Claim source ownership for structural properties when remote node
                    // management is enabled, so changes are captured by the ChangeQueueProcessor.
                    if (_configuration.EnableRemoteNodeManagement)
                    {
                        _ownership.ClaimSource(property.Reference);
                    }

                    await LoadSubjectDictionaryAsync(property, childNodeId, monitoredItems, session, loadedSubjects, subjectsByNodeId, subjectMap, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    MonitorValueNode(childNodeId, property, monitoredItems);
                    var visitedNodes = new HashSet<NodeId>();
                    await LoadAttributeNodesAsync(property, childNodeId, session, monitoredItems, visitedNodes, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task LoadAttributeNodesAsync(
        RegisteredSubjectProperty property,
        NodeId parentNodeId,
        ISession session,
        List<MonitoredItem> monitoredItems,
        HashSet<NodeId> visitedNodes,
        CancellationToken cancellationToken)
    {
        // Guard against cycles in OPC UA hierarchy
        if (!visitedNodes.Add(parentNodeId))
            return;

        // Browse children of the variable node
        var childNodes = await BrowseNodeAsync(parentNodeId, session, cancellationToken).ConfigureAwait(false);
        var matchedNames = new HashSet<string>();

        // First pass: match known attributes from C# model
        foreach (var attribute in property.Attributes)
        {
            var attributeConfiguration = _configuration.NodeMapper.TryGetNodeConfiguration(attribute);
            if (attributeConfiguration is null)
                continue;

            var attributeBrowseName = attributeConfiguration.BrowseName ?? attribute.BrowseName;
            ReferenceDescription? matchingNode = null;
            foreach (var childNode in childNodes)
            {
                if (childNode.BrowseName.Name == attributeBrowseName)
                {
                    matchingNode = childNode;
                    break;
                }
            }

            if (matchingNode is null)
                continue;

            matchedNames.Add(attributeBrowseName);
            var attributeNodeId = ExpandedNodeId.ToNodeId(matchingNode.NodeId, session.NamespaceUris);
            MonitorValueNode(attributeNodeId, attribute, monitoredItems);

            // Recursive: attributes can have attributes
            await LoadAttributeNodesAsync(attribute, attributeNodeId, session, monitoredItems, visitedNodes, cancellationToken).ConfigureAwait(false);
        }

        // Second pass: add dynamic attributes (same pattern as ShouldAddDynamicProperty)
        foreach (var childNode in childNodes)
        {
            if (childNode.NodeClass != NodeClass.Variable)
                continue;

            var browseName = childNode.BrowseName.Name;
            if (matchedNames.Contains(browseName))
                continue;

            var addAsDynamic = _configuration.ShouldAddDynamicAttribute is not null &&
                await _configuration.ShouldAddDynamicAttribute(childNode, cancellationToken).ConfigureAwait(false);
            if (!addAsDynamic)
                continue;

            var inferredType = await _configuration.TypeResolver.TryGetTypeForNodeAsync(session, childNode, cancellationToken).ConfigureAwait(false);
            if (inferredType is null)
            {
                _logger.LogWarning(
                    "Could not infer type for dynamic attribute '{AttributeName}' on property '{PropertyName}' (NodeId: {NodeId}). Skipping attribute.",
                    browseName, property.Name, childNode.NodeId);
                continue;
            }

            // Same closure pattern as dynamic properties
            object? value = null;
            var dynamicAttribute = property.AddAttribute(
                browseName,
                inferredType,
                _ => value,
                (_, o) => value = o,
                _configuration.TypeResolver.GetDynamicPropertyAttributes(childNode, session));

            var attributeNodeId = ExpandedNodeId.ToNodeId(childNode.NodeId, session.NamespaceUris);
            MonitorValueNode(attributeNodeId, dynamicAttribute, monitoredItems);

            // Recursive with cycle detection
            await LoadAttributeNodesAsync(dynamicAttribute, attributeNodeId, session, monitoredItems, visitedNodes, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task LoadSubjectReferenceAsync(RegisteredSubjectProperty property,
        ReferenceDescription nodeReference,
        IInterceptorSubject subject,
        ISession session,
        List<MonitoredItem> monitoredItems,
        HashSet<IInterceptorSubject> loadedSubjects,
        Dictionary<NodeId, IInterceptorSubject> subjectsByNodeId,
        ConnectorSubjectMap<NodeId>? subjectMap,
        CancellationToken cancellationToken)
    {
        var existingSubject = property.Children.SingleOrDefault();
        if (existingSubject.Subject is not null)
        {
            await LoadSubjectAsync(existingSubject.Subject, nodeReference, session, monitoredItems, loadedSubjects, subjectsByNodeId, subjectMap, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var nodeId = ExpandedNodeId.ToNodeId(nodeReference.NodeId, session.NamespaceUris);
            if (nodeId is not null && subjectsByNodeId.TryGetValue(nodeId, out var reusedSubject))
            {
                property.SetValueFromSource(_source, null, null, reusedSubject);
            }
            else
            {
                var newSubject = await _configuration.SubjectFactory.CreateSubjectAsync(property, nodeReference, session, cancellationToken).ConfigureAwait(false);
                newSubject.Context.AddFallbackContext(subject.Context);

                if (nodeId is not null)
                {
                    subjectsByNodeId[nodeId] = newSubject;
                }

                await LoadSubjectAsync(newSubject, nodeReference, session, monitoredItems, loadedSubjects, subjectsByNodeId, subjectMap, cancellationToken).ConfigureAwait(false);
                property.SetValueFromSource(_source, null, null, newSubject);
            }
        }
    }

    private async Task LoadSubjectCollectionAsync(RegisteredSubjectProperty property,
        NodeId childNodeId,
        List<MonitoredItem> monitoredItems,
        ISession session,
        HashSet<IInterceptorSubject> loadedSubjects,
        Dictionary<NodeId, IInterceptorSubject> subjectsByNodeId,
        ConnectorSubjectMap<NodeId>? subjectMap,
        CancellationToken cancellationToken)
    {
        var childNodes = await BrowseNodeAsync(childNodeId, session, cancellationToken).ConfigureAwait(false);
        var childCount = childNodes.Count;
        var children = new List<(ReferenceDescription Node, IInterceptorSubject Subject)>(childCount);

        // Convert to array once to avoid multiple enumerations
        var existingChildren = property.Children.ToArray();

        for (var i = 0; i < childCount; i++)
        {
            var childNode = childNodes[i];
            var childSubject = i < existingChildren.Length ? existingChildren[i].Subject : null;
            childSubject ??= await _configuration.SubjectFactory.CreateCollectionSubjectAsync(
                property, childNode, i, session, cancellationToken).ConfigureAwait(false);

            children.Add((childNode, childSubject));
        }

        var collection = DefaultSubjectFactory.Instance
            .CreateSubjectCollection(property.Type, children.Select(c => c.Subject));

        property.SetValueFromSource(_source, null, null, collection);

        // TODO(perf): Consider parallelizing child subject loading with Task.WhenAll.
        // Requires making monitoredItems and loadedSubjects thread-safe (e.g., ConcurrentBag, ConcurrentHashSet).
        foreach (var child in children)
        {
            await LoadSubjectAsync(child.Subject, child.Node, session, monitoredItems, loadedSubjects, subjectsByNodeId, subjectMap, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task LoadSubjectDictionaryAsync(
        RegisteredSubjectProperty property,
        NodeId childNodeId,
        List<MonitoredItem> monitoredItems,
        ISession session,
        HashSet<IInterceptorSubject> loadedSubjects,
        Dictionary<NodeId, IInterceptorSubject> subjectsByNodeId,
        ConnectorSubjectMap<NodeId>? subjectMap,
        CancellationToken cancellationToken)
    {
        var childNodes = await BrowseNodeAsync(childNodeId, session, cancellationToken).ConfigureAwait(false);
        var existingChildren = property.Children.ToDictionary(c => c.Index!, c => c.Subject);
        var entries = new Dictionary<object, IInterceptorSubject>();
        var nodesByKey = new Dictionary<object, ReferenceDescription>();

        foreach (var childNode in childNodes)
        {
            var key = childNode.BrowseName.Name; // Use BrowseName as dictionary key
            var childSubject = existingChildren.GetValueOrDefault(key)
                ?? await _configuration.SubjectFactory.CreateCollectionSubjectAsync(
                    property, childNode, key, session, cancellationToken).ConfigureAwait(false);
            entries[key] = childSubject;
            nodesByKey[key] = childNode;
        }

        var dictionary = DefaultSubjectFactory.Instance.CreateSubjectDictionary(property.Type, entries);
        property.SetValueFromSource(_source, null, null, dictionary);

        foreach (var entry in entries)
        {
            var childNode = nodesByKey[entry.Key];
            await LoadSubjectAsync(entry.Value, childNode, session, monitoredItems, loadedSubjects, subjectsByNodeId, subjectMap, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<RegisteredSubjectProperty?> FindSubjectPropertyAsync(
        RegisteredSubject registeredSubject,
        ReferenceDescription nodeReference,
        ISession session,
        CancellationToken cancellationToken)
    {
        // Use NodeMapper for property lookup (supports attributes, path provider, and fluent config)
        return await _configuration.NodeMapper.TryGetPropertyAsync(
            registeredSubject, nodeReference, session, cancellationToken).ConfigureAwait(false);
    }

    private void MonitorValueNode(NodeId nodeId, RegisteredSubjectProperty property, List<MonitoredItem> monitoredItems)
    {
        var monitoredItem = MonitoredItemFactory.Create(_configuration, nodeId, property);
        property.Reference.SetPropertyData(_source.OpcUaNodeIdKey, nodeId);

        if (!_ownership.ClaimSource(property.Reference))
        {
            _logger.LogError(
                "Property {Subject}.{Property} already owned by another source. Skipping OPC UA monitoring.",
                property.Subject.GetType().Name, property.Name);
            return;
        }

        monitoredItems.Add(monitoredItem);
    }

    private async Task LoadVariableNodeForSubjectAsync(
        RegisteredSubjectProperty property,
        NodeId nodeId,
        ISession session,
        List<MonitoredItem> monitoredItems,
        CancellationToken cancellationToken)
    {
        var childSubject = property.Children.SingleOrDefault().Subject?.TryGetRegisteredSubject();
        if (childSubject == null)
        {
            return;
        }

        // Find [OpcUaValue] property and monitor the node for it
        var valueProperty = childSubject.TryGetValueProperty(_configuration.NodeMapper);
        if (valueProperty != null)
        {
            MonitorValueNode(nodeId, valueProperty, monitoredItems);
        }

        // Also load HasProperty children as regular variable nodes
        var childNodes = await BrowseNodeAsync(nodeId, session, cancellationToken).ConfigureAwait(false);
        foreach (var childNode in childNodes)
        {
            // Find matching property in child subject (excluding the value property)
            foreach (var childProperty in childSubject.Properties)
            {
                if (childProperty == valueProperty)
                {
                    continue; // Skip the value property
                }

                var childPropertyName = childProperty.ResolvePropertyName(_configuration.NodeMapper);
                if (childPropertyName == childNode.BrowseName.Name)
                {
                    var childNodeId = ExpandedNodeId.ToNodeId(childNode.NodeId, session.NamespaceUris);
                    MonitorValueNode(childNodeId, childProperty, monitoredItems);
                    break;
                }
            }
        }
    }

    private async Task<ReferenceDescriptionCollection> BrowseNodeAsync(
        NodeId nodeId,
        ISession session,
        CancellationToken cancellationToken)
    {
        var browseDescriptions = new BrowseDescriptionCollection
        {
            new BrowseDescription
            {
                NodeId = nodeId,
                BrowseDirection = BrowseDirection.Forward,
                ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
                IncludeSubtypes = true,
                NodeClassMask = NodeClassMask,
                ResultMask = (uint)BrowseResultMask.All
            }
        };

        var results = new ReferenceDescriptionCollection();

        var response = await session.BrowseAsync(
            null,
            null,
            _configuration.MaximumReferencesPerNode,
            browseDescriptions,
            cancellationToken).ConfigureAwait(false);

        if (response.Results.Count > 0 && StatusCode.IsGood(response.Results[0].StatusCode))
        {
            results.AddRange(response.Results[0].References);

            var continuationPoint = response.Results[0].ContinuationPoint;
            while (continuationPoint is { Length: > 0 })
            {
                var nextResponse = await session.BrowseNextAsync(
                    null, false,
                    [continuationPoint], cancellationToken).ConfigureAwait(false);

                if (nextResponse.Results.Count > 0 && StatusCode.IsGood(nextResponse.Results[0].StatusCode))
                {
                    var r0 = nextResponse.Results[0];
                    if (r0.References is { Count: > 0 } nextReferences)
                    {
                        foreach (var reference in nextReferences)
                        {
                            results.Add(reference);
                        }
                    }
                    continuationPoint = r0.ContinuationPoint;
                }
                else
                {
                    break;
                }
            }
        }

        return results;
    }
}
