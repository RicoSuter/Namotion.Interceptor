using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Attributes;
using Namotion.Interceptor.OpcUa.Mapping;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;
using Opc.Ua.Server;

namespace Namotion.Interceptor.OpcUa.Server;

internal class CustomNodeManager : CustomNodeManager2
{
    private const string PathDelimiter = ".";

    private readonly IInterceptorSubject _subject;
    private readonly OpcUaServerConfiguration _configuration;
    private readonly IOpcUaNodeMapper _nodeMapper;
    private readonly ILogger _logger;
    private readonly OpcUaNodeFactory _nodeFactory;
    private readonly OpcUaServerNodeCreator _nodeCreator;
    private readonly OpcUaServerGraphChangeReceiver _graphChangeProcessor;

    private readonly SemaphoreSlim _structureLock = new(1, 1);
    private readonly SubjectConnectorRegistry<NodeId, NodeState> _subjectRegistry = new();
    private readonly OpcUaServerGraphChangePublisher _modelChangePublisher;

    public CustomNodeManager(
        IInterceptorSubject subject,
        OpcUaSubjectServerBackgroundService source,
        IServerInternal server,
        ApplicationConfiguration applicationConfiguration,
        OpcUaServerConfiguration configuration,
        ILogger logger) :
        base(server, applicationConfiguration, configuration.GetNamespaceUris())
    {
        _subject = subject;
        _configuration = configuration;
        _nodeMapper = configuration.NodeMapper;
        _logger = logger;
        _nodeFactory = new OpcUaNodeFactory(logger);
        _modelChangePublisher = new OpcUaServerGraphChangePublisher(logger);
        _nodeCreator = new OpcUaServerNodeCreator(this, configuration, _nodeFactory, source, _subjectRegistry, _modelChangePublisher, logger);

        var externalNodeValidator = new OpcUaServerExternalNodeValidator(configuration, logger);
        _graphChangeProcessor = new OpcUaServerGraphChangeReceiver(
            _subject,
            _configuration,
            _subjectRegistry,
            externalNodeValidator,
            this,
            source,
            _logger);
    }

    // Expose protected members for OpcUaNodeFactory
    internal ISystemContext GetSystemContext() => SystemContext;
    internal NodeIdDictionary<NodeState> GetPredefinedNodes() => PredefinedNodes;
    internal NodeState FindNode(NodeId nodeId) => FindNodeInAddressSpace(nodeId);
    internal void AddNode(NodeState node) => AddPredefinedNode(SystemContext, node);

    protected override NodeStateCollection LoadPredefinedNodes(ISystemContext context)
    {
        var collection = base.LoadPredefinedNodes(context);
        _configuration.LoadPredefinedNodes(collection, context);
        return collection;
    }

    public void ClearPropertyData()
    {
        var rootSubject = _subject.TryGetRegisteredSubject();
        if (rootSubject != null)
        {
            foreach (var property in rootSubject.Properties)
            {
                property.Reference.RemovePropertyData(OpcUaSubjectServerBackgroundService.OpcVariableKey);
                ClearAttributePropertyData(property);
            }
        }

        foreach (var subject in _subjectRegistry.GetAllSubjects())
        {
            var registeredSubject = subject.TryGetRegisteredSubject();
            if (registeredSubject != null)
            {
                foreach (var property in registeredSubject.Properties)
                {
                    property.Reference.RemovePropertyData(OpcUaSubjectServerBackgroundService.OpcVariableKey);
                    ClearAttributePropertyData(property);
                }
            }
        }
    }

    private void ClearAttributePropertyData(RegisteredSubjectProperty property)
    {
        foreach (var attribute in property.Attributes)
        {
            attribute.Reference.RemovePropertyData(OpcUaSubjectServerBackgroundService.OpcVariableKey);
            ClearAttributePropertyData(attribute);
        }
    }

    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        base.CreateAddressSpace(externalReferences);

        _structureLock.Wait();
        try
        {
            var registeredSubject = _subject.TryGetRegisteredSubject();
            if (registeredSubject is not null)
            {
                if (_configuration.RootName is not null)
                {
                    var node = _nodeFactory.CreateFolderNode(this, ObjectIds.ObjectsFolder,
                        new NodeId(_configuration.RootName, NamespaceIndex), new QualifiedName(_configuration.RootName, NamespaceIndex), null, null, null);

                    _nodeCreator.CreateSubjectNodes(node.NodeId, registeredSubject, _configuration.RootName + PathDelimiter);
                }
                else
                {
                    _nodeCreator.CreateSubjectNodes(ObjectIds.ObjectsFolder, registeredSubject, string.Empty);
                }
            }
        }
        finally
        {
            _structureLock.Release();
        }
    }

    /// <summary>
    /// Removes nodes for a detached subject. Idempotent - safe to call multiple times.
    /// Uses DeleteNode to properly cleanup nodes and event handlers, preventing memory leaks.
    /// Uses reference counting - only deletes the node when the last reference is removed.
    /// For shared subjects (multiple references), removes only the reference from the specified parent and sends ReferenceDeleted.
    /// </summary>
    /// <param name="subject">The subject being removed.</param>
    /// <param name="sourceProperty">The property from which the subject is being removed (for shared subject reference removal).</param>
    public void RemoveSubjectNodes(IInterceptorSubject subject, RegisteredSubjectProperty? sourceProperty = null)
    {
        _structureLock.Wait();
        try
        {
            RemoveSubjectNodesCore(subject, sourceProperty);
        }
        finally
        {
            _structureLock.Release();
        }
    }

    /// <summary>
    /// Removes subject nodes and re-indexes collection BrowseNames atomically under a single lock.
    /// This prevents race conditions between removal and reindexing when handling concurrent requests.
    /// </summary>
    /// <param name="subject">The subject being removed.</param>
    /// <param name="property">The property from which the subject is being removed.</param>
    public void RemoveSubjectNodesAndReindex(IInterceptorSubject subject, RegisteredSubjectProperty property)
    {
        _structureLock.Wait();
        try
        {
            RemoveSubjectNodesCore(subject, property);
            if (property.IsSubjectCollection)
            {
                ReindexCollectionBrowseNamesCore(property);
            }
        }
        finally
        {
            _structureLock.Release();
        }
    }

    /// <summary>
    /// Core implementation of RemoveSubjectNodes. Must be called while holding _structureLock.
    /// </summary>
    private void RemoveSubjectNodesCore(IInterceptorSubject subject, RegisteredSubjectProperty? sourceProperty)
    {
        // Get existing node state BEFORE unregistering
        _subjectRegistry.TryGetData(subject, out var existingNodeState);

        // Unregister and check if last
        _subjectRegistry.Unregister(subject, out _, out var nodeState, out var isLast);

        _logger.LogDebug("RemoveSubjectNodes: subject={SubjectType}, isLast={IsLast}, hasNodeState={HasNodeState}, nodeId={NodeId}",
            subject.GetType().Name, isLast, nodeState is not null || existingNodeState is not null,
            (nodeState ?? existingNodeState)?.NodeId?.ToString() ?? "null");

        if (isLast && nodeState is not null)
        {
            var registeredSubject = subject.TryGetRegisteredSubject();

            // Remove variable nodes for this subject's properties
            if (registeredSubject != null)
            {
                foreach (var property in registeredSubject.Properties)
                {
                    if (property.Reference.TryGetPropertyData(OpcUaSubjectServerBackgroundService.OpcVariableKey, out var node)
                        && node is BaseDataVariableState variableNode)
                    {
                        DeleteNode(SystemContext, variableNode.NodeId);
                        property.Reference.RemovePropertyData(OpcUaSubjectServerBackgroundService.OpcVariableKey);
                    }
                }
            }

            // Remove object node and its references
            RemoveNodeAndReferences(subject, nodeState);

            // Queue model change event for node deletion
            _modelChangePublisher.QueueChange(nodeState.NodeId, ModelChangeStructureVerbMask.NodeDeleted);
        }
        else if (!isLast && existingNodeState is not null)
        {
            // Shared subject: removed from one parent but still exists in others
            // Remove the reference from the specific parent container
            if (sourceProperty is not null)
            {
                RemoveReferenceFromParentProperty(sourceProperty, existingNodeState);
            }

            // Send ReferenceDeleted so clients know to remove from their collections
            _logger.LogDebug(
                "RemoveSubjectNodes: Shared subject {SubjectType} removed from one parent, sending ReferenceDeleted for NodeId {NodeId}",
                subject.GetType().Name, existingNodeState.NodeId);
            _modelChangePublisher.QueueChange(existingNodeState.NodeId, ModelChangeStructureVerbMask.ReferenceDeleted);
        }
    }

    /// <summary>
    /// Removes a child node reference from a specific parent property's container.
    /// Used for shared subjects where the node still exists but needs to be removed from one parent's collection.
    /// </summary>
    private void RemoveReferenceFromParentProperty(RegisteredSubjectProperty parentProperty, NodeState childNodeState)
    {
        var parentSubject = parentProperty.Subject;
        if (parentSubject is null)
        {
            return;
        }

        // Get the parent subject's node - handle root subject specially
        NodeState? parentNodeState = null;
        if (ReferenceEquals(parentSubject, _subject))
        {
            // Parent is the root subject - get the root node
            var rootNodeId = _configuration.RootName is not null
                ? new NodeId(_configuration.RootName, NamespaceIndex)
                : ObjectIds.ObjectsFolder;
            parentNodeState = FindNodeInAddressSpace(rootNodeId);
        }
        else if (!_subjectRegistry.TryGetData(parentSubject, out parentNodeState) || parentNodeState is null)
        {
            _logger.LogDebug(
                "RemoveReferenceFromParentProperty: Parent subject {ParentType} has no node state",
                parentSubject.GetType().Name);
            return;
        }

        if (parentNodeState is null)
        {
            _logger.LogDebug(
                "RemoveReferenceFromParentProperty: Could not find node state for parent subject {ParentType}",
                parentSubject.GetType().Name);
            return;
        }

        // For collection/dictionary properties, the parent is the container node
        var propertyName = parentProperty.ResolvePropertyName(_nodeMapper);
        if (propertyName is null)
        {
            return;
        }

        NodeState? containerNode = null;

        if (parentProperty.IsSubjectCollection || parentProperty.IsSubjectDictionary)
        {
            // Check for flat collection mode
            var nodeConfiguration = _nodeMapper.TryGetNodeConfiguration(parentProperty);
            var isFlat = nodeConfiguration?.CollectionStructure == CollectionNodeStructure.Flat;

            if (isFlat)
            {
                // Flat mode: container is the parent node itself
                containerNode = parentNodeState;
            }
            else
            {
                // Container mode: need to find the container node by path-based NodeId
                // Container nodes have IDs like "Root.PropertyName"
                string containerPath;
                if (parentNodeState.NodeId.IdType == IdType.String &&
                    parentNodeState.NodeId.Identifier is string parentPath)
                {
                    containerPath = $"{parentPath}.{propertyName}";
                }
                else if (ReferenceEquals(parentSubject, _subject) && _configuration.RootName is not null)
                {
                    containerPath = $"{_configuration.RootName}.{propertyName}";
                }
                else
                {
                    containerPath = propertyName;
                }

                var containerNodeId = new NodeId(containerPath, NamespaceIndex);
                containerNode = FindNodeInAddressSpace(containerNodeId);
            }
        }
        else if (parentProperty.IsSubjectReference)
        {
            // Direct reference: container is the parent node
            containerNode = parentNodeState;
        }

        if (containerNode is null)
        {
            _logger.LogDebug(
                "RemoveReferenceFromParentProperty: Container not found for {ParentType}.{Property}",
                parentSubject.GetType().Name, parentProperty.Name);
            return;
        }

        // Remove the reference from the container to the child
        // For shared subjects added via AddReference, we need to remove the reference
        var referenceTypeId = ReferenceTypeIds.HasComponent; // Default, could be configured

        // First try to remove the child if it was added as a child node
        if (childNodeState is BaseInstanceState instanceState)
        {
            containerNode.RemoveChild(instanceState);
        }

        // Also remove any explicit references (for shared subjects)
        containerNode.RemoveReference(referenceTypeId, false, childNodeState.NodeId);

        _logger.LogDebug(
            "RemoveReferenceFromParentProperty: Removed reference from {ContainerNodeId} to {ChildNodeId}",
            containerNode.NodeId, childNodeState.NodeId);
    }

    /// <summary>
    /// Removes a node and all references pointing to it from parent nodes.
    /// This is necessary because the OPC UA SDK's DeleteNode only removes the node itself,
    /// but leaves references from parent nodes intact, causing browse operations to still return the deleted node.
    /// </summary>
    /// <param name="subject">The subject whose node is being removed.</param>
    /// <param name="nodeState">The node to remove.</param>
    private void RemoveNodeAndReferences(IInterceptorSubject subject, NodeState nodeState)
    {
        var nodeId = nodeState.NodeId;

        // Find parent node using model-based parent lookup
        var parentNodeId = GetParentNodeId(subject);
        if (parentNodeId is not null)
        {
            var parentNode = FindNodeInAddressSpace(parentNodeId);
            if (parentNode is not null && nodeState is BaseInstanceState instanceState)
            {
                // Remove child from parent using RemoveChild which handles both
                // the parent-child relationship and the reference
                parentNode.RemoveChild(instanceState);

                _logger.LogDebug(
                    "Removed child node '{NodeId}' from parent '{ParentNodeId}'.",
                    nodeId, parentNodeId);
            }
        }

        // Delete the node from the address space
        DeleteNode(SystemContext, nodeId);
    }

    /// <summary>
    /// Gets the parent node ID for a subject using model-based parent tracking.
    /// Uses RegisteredSubject.Parents to find the parent subject, then looks up its NodeId.
    /// </summary>
    /// <param name="subject">The subject to find the parent for.</param>
    /// <returns>The parent NodeId, or null if no parent is found.</returns>
    private NodeId? GetParentNodeId(IInterceptorSubject subject)
    {
        var registered = subject.TryGetRegisteredSubject();
        // Note: Taking Parents[0] is safe here because this method is only called
        // when removing the last reference (isLast=true). At that point, the
        // SubjectRegistry has already processed all previous parent removals,
        // leaving exactly one parent in the array - the current one being removed.
        if (registered?.Parents.Length > 0)
        {
            var parentProperty = registered.Parents[0].Property;
            var parentSubject = parentProperty.Parent.Subject;

            // Check collection structure mode for collections
            var nodeConfiguration = _nodeMapper.TryGetNodeConfiguration(parentProperty);
            var isContainerMode = parentProperty.IsSubjectDictionary ||
                (parentProperty.IsSubjectCollection &&
                 (nodeConfiguration?.CollectionStructure ?? Attributes.CollectionNodeStructure.Container) == Attributes.CollectionNodeStructure.Container);

            // Check if parent is the root subject
            if (ReferenceEquals(parentSubject, _subject))
            {
                // For collection/dictionary items in Container mode, parent is the container node
                if (isContainerMode)
                {
                    var propertyName = parentProperty.ResolvePropertyName(_nodeMapper);
                    if (propertyName is not null)
                    {
                        var rootPath = _configuration.RootName is not null
                            ? $"{_configuration.RootName}.{propertyName}"
                            : propertyName;
                        return new NodeId(rootPath, NamespaceIndex);
                    }
                }

                // For direct references or Flat mode collections, parent is the root node
                return _configuration.RootName is not null
                    ? new NodeId(_configuration.RootName, NamespaceIndex)
                    : ObjectIds.ObjectsFolder;
            }

            // Look up the parent subject's node
            if (_subjectRegistry.TryGetData(parentSubject, out var parentNodeState) && parentNodeState is not null)
            {
                // For collection/dictionary items in Container mode, parent is the container node
                if (isContainerMode)
                {
                    var propertyName = parentProperty.ResolvePropertyName(_nodeMapper);
                    if (propertyName is not null && parentNodeState.NodeId.Identifier is string parentPath)
                    {
                        return new NodeId($"{parentPath}.{propertyName}", NamespaceIndex);
                    }
                }

                // For direct references or Flat mode collections, parent is the subject's node
                return parentNodeState.NodeId;
            }
        }

        return null;
    }

    /// <summary>
    /// Flushes all pending model change events to clients.
    /// Emits a GeneralModelChangeEvent containing all batched changes.
    /// Called after a batch of structural changes has been processed.
    /// </summary>
    public void FlushModelChangeEvents()
    {
        _modelChangePublisher.Flush(Server, SystemContext);
    }

    /// <summary>
    /// Gets the parent NodeId and path for a given parent subject.
    /// Centralizes the logic for determining where to attach child nodes.
    /// </summary>
    /// <param name="parentSubject">The parent subject to look up.</param>
    /// <returns>The parent NodeId and path prefix, or (null, empty) if parent not found.</returns>
    private (NodeId? ParentNodeId, string ParentPath) GetParentNodeIdAndPath(IInterceptorSubject parentSubject)
    {
        if (ReferenceEquals(parentSubject, _subject))
        {
            if (_configuration.RootName is not null)
            {
                return (new NodeId(_configuration.RootName, NamespaceIndex),
                        _configuration.RootName + PathDelimiter);
            }

            return (ObjectIds.ObjectsFolder, string.Empty);
        }

        if (_subjectRegistry.TryGetData(parentSubject, out var parentNode) && parentNode is not null)
        {
            var path = parentNode.NodeId.Identifier is string stringId
                ? stringId + PathDelimiter
                : string.Empty;
            return (parentNode.NodeId, path);
        }

        return (null, string.Empty);
    }

    /// <summary>
    /// Re-indexes collection BrowseNames and NodeIds after an item has been removed.
    /// Updates all remaining items to have sequential indices starting from 0.
    /// This ensures BrowseNames like "People[0]", "People[1]" remain contiguous.
    /// Also updates the NodeIds (which are path-based) to prevent conflicts when new items are added.
    /// Must be called while holding _structureLock.
    /// </summary>
    /// <param name="property">The collection property whose children should be re-indexed.</param>
    private void ReindexCollectionBrowseNamesCore(RegisteredSubjectProperty property)
    {
        var propertyName = property.ResolvePropertyName(_nodeMapper);
        if (propertyName is null)
        {
            return;
        }

        var parentSubject = property.Parent.Subject;
        var (parentNodeId, parentPath) = GetParentNodeIdAndPath(parentSubject);
        if (parentNodeId is null)
        {
            _logger.LogWarning("Cannot reindex: parent node not found for property '{PropertyName}'.", property.Name);
            return;
        }

        var children = property.Children.ToList();
        for (var i = 0; i < children.Count; i++)
        {
            var subject = children[i].Subject;
            if (_subjectRegistry.TryGetData(subject, out var nodeState) && nodeState is not null)
            {
                var newBrowseName = new QualifiedName($"{propertyName}[{i}]", NamespaceIndex);
                var browseNameChanged = !nodeState.BrowseName.Equals(newBrowseName);

                // Calculate new NodeId path - same for both Flat and Container modes
                // (the difference is only the OPC UA parent node, not the NodeId path)
                var newPath = $"{parentPath}{propertyName}[{i}]";
                var newNodeId = new NodeId(newPath, NamespaceIndex);
                var nodeIdChanged = !nodeState.NodeId.Equals(newNodeId);

                if (browseNameChanged || nodeIdChanged)
                {
                    var oldNodeId = nodeState.NodeId;

                    // Update BrowseName
                    if (browseNameChanged)
                    {
                        nodeState.BrowseName = newBrowseName;
                    }

                    // Update NodeId - this is critical for preventing conflicts
                    if (nodeIdChanged)
                    {
                        // Update the PredefinedNodes dictionary (keyed by NodeId)
                        var predefinedNodes = GetPredefinedNodes();
                        if (predefinedNodes.ContainsKey(oldNodeId))
                        {
                            predefinedNodes.Remove(oldNodeId);
                            nodeState.NodeId = newNodeId;
                            predefinedNodes[newNodeId] = nodeState;
                            // Update the registry's external ID mapping for O(1) bidirectional lookup
                            _subjectRegistry.UpdateExternalId(subject, newNodeId);
                        }
                    }

                    _logger.LogDebug(
                        "Re-indexed collection item: BrowseName='{BrowseName}', NodeId='{OldNodeId}' -> '{NewNodeId}'.",
                        newBrowseName, oldNodeId, newNodeId);
                }
            }
        }
    }

    /// <summary>
    /// Creates a node for a subject that was added to a property at runtime.
    /// Determines parent node and path from the property context.
    /// </summary>
    /// <param name="property">The property that the subject was added to.</param>
    /// <param name="subject">The subject to create a node for.</param>
    /// <param name="index">The collection/dictionary index, or null for single references.</param>
    public void CreateSubjectNode(RegisteredSubjectProperty property, IInterceptorSubject subject, object? index)
    {
        _structureLock.Wait();
        try
        {
            var parentSubject = property.Parent.Subject;
            var (parentNodeId, parentPath) = GetParentNodeIdAndPath(parentSubject);
            if (parentNodeId is null)
            {
                _logger.LogWarning(
                    "Cannot create node for subject on property '{PropertyName}': parent node not found.",
                    property.Name);
                return;
            }

            var propertyName = property.ResolvePropertyName(_nodeMapper);
            if (propertyName is null)
            {
                return;
            }

            var nodeConfiguration = _nodeMapper.TryGetNodeConfiguration(property);

            if (property.IsSubjectCollection)
            {
                var childIndex = index ?? property.Children.Length - 1;

                // Check collection structure mode - default is Container for backward compatibility
                var collectionStructure = nodeConfiguration?.CollectionStructure ?? Attributes.CollectionNodeStructure.Container;
                if (collectionStructure == Attributes.CollectionNodeStructure.Flat)
                {
                    // Flat mode: create directly under parent node
                    _nodeCreator.CreateCollectionChildNode(property, subject, childIndex, propertyName, parentPath, parentNodeId, nodeConfiguration);
                }
                else
                {
                    // Container mode: create under container folder
                    var containerNode = _nodeCreator.GetOrCreateContainerNode(propertyName, nodeConfiguration, parentNodeId, parentPath);
                    _nodeCreator.CreateCollectionChildNode(property, subject, childIndex, propertyName, parentPath, containerNode.NodeId, nodeConfiguration);
                }
            }
            else if (property.IsSubjectDictionary)
            {
                var containerNode = _nodeCreator.GetOrCreateContainerNode(propertyName, nodeConfiguration, parentNodeId, parentPath);
                if (!_nodeCreator.CreateDictionaryChildNode(property, subject, index, propertyName, parentPath, containerNode.NodeId, nodeConfiguration))
                {
                    return;
                }
            }
            else if (property.IsSubjectReference)
            {
                _nodeCreator.CreateSubjectReferenceNode(propertyName, property, subject, index, parentNodeId, parentPath, nodeConfiguration);
            }
        }
        finally
        {
            _structureLock.Release();
        }
    }

    #region External Node Management (AddNodes/DeleteNodes)

    /// <summary>
    /// Adds a subject from an external AddNodes request.
    /// Creates a new subject instance and adds it to the appropriate parent property in the model.
    /// </summary>
    /// <param name="typeDefinitionId">The OPC UA TypeDefinition NodeId.</param>
    /// <param name="browseName">The BrowseName for the new node.</param>
    /// <param name="parentNodeId">The parent node's NodeId.</param>
    /// <returns>The created subject and its node, or null if creation failed.</returns>
    public Task<(IInterceptorSubject? Subject, NodeState? Node)> AddSubjectFromExternalAsync(
        NodeId typeDefinitionId,
        QualifiedName browseName,
        NodeId parentNodeId)
        => _graphChangeProcessor.AddSubjectFromExternalAsync(typeDefinitionId, browseName, parentNodeId);

    /// <summary>
    /// Removes a subject based on its NodeId from an external DeleteNodes request.
    /// </summary>
    /// <param name="nodeId">The NodeId of the node to delete.</param>
    /// <returns>True if the subject was found and removed, false otherwise.</returns>
    public bool RemoveSubjectFromExternal(NodeId nodeId)
        => _graphChangeProcessor.RemoveSubjectFromExternal(nodeId);

    /// <summary>
    /// Gets whether external node management is enabled for this server.
    /// </summary>
    public bool IsExternalNodeManagementEnabled => _graphChangeProcessor.IsEnabled;

    #endregion
}