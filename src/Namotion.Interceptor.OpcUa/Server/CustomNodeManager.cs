using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Attributes;
using Namotion.Interceptor.OpcUa.Mapping;
using Namotion.Interceptor.OpcUa.Server.Graph;
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
    private readonly ExternalNodeManagementHelper _externalNodeManagementHelper;
    private readonly NodeCreator _nodeCreator;

    private readonly SemaphoreSlim _structureLock = new(1, 1);
    private readonly ConnectorReferenceCounter<NodeState> _subjectRefCounter = new();
    private readonly ModelChangePublisher _modelChangePublisher;

    // Mapping from NodeId to the subject that was created for external AddNodes requests
    private readonly Dictionary<NodeId, IInterceptorSubject> _externallyAddedSubjects = new();
    private readonly Lock _externallyAddedSubjectsLock = new();

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
        _externalNodeManagementHelper = new ExternalNodeManagementHelper(configuration, logger);
        _modelChangePublisher = new ModelChangePublisher(logger);
        _nodeCreator = new NodeCreator(this, configuration, _nodeFactory, source, _subjectRefCounter, _modelChangePublisher, logger);
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

        foreach (var (subject, _) in _subjectRefCounter.GetAllEntries())
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
            // Get the node state BEFORE decrementing (needed for ReferenceDeleted when not last)
            _subjectRefCounter.TryGetData(subject, out var existingNodeState);

            // Decrement reference count and check if this was the last reference
            var isLast = _subjectRefCounter.DecrementAndCheckLast(subject, out var nodeState);

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
        finally
        {
            _structureLock.Release();
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
        else if (!_subjectRefCounter.TryGetData(parentSubject, out parentNodeState) || parentNodeState is null)
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
            if (_subjectRefCounter.TryGetData(parentSubject, out var parentNodeState) && parentNodeState is not null)
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
    /// Re-indexes collection BrowseNames after an item has been removed.
    /// Updates all remaining items to have sequential indices starting from 0.
    /// This ensures BrowseNames like "People[0]", "People[1]" remain contiguous.
    /// </summary>
    /// <param name="property">The collection property whose children should be re-indexed.</param>
    public void ReindexCollectionBrowseNames(RegisteredSubjectProperty property)
    {
        if (!property.IsSubjectCollection)
        {
            return;
        }

        _structureLock.Wait();
        try
        {
            var propertyName = property.ResolvePropertyName(_nodeMapper);
            if (propertyName is null)
            {
                return;
            }

            var children = property.Children.ToList();
            for (var i = 0; i < children.Count; i++)
            {
                if (_subjectRefCounter.TryGetData(children[i].Subject, out var nodeState) && nodeState is not null)
                {
                    var newBrowseName = new QualifiedName($"{propertyName}[{i}]", NamespaceIndex);

                    // Only update if the BrowseName has actually changed
                    if (!nodeState.BrowseName.Equals(newBrowseName))
                    {
                        nodeState.BrowseName = newBrowseName;

                        _logger.LogDebug(
                            "Re-indexed collection item BrowseName to '{BrowseName}'.",
                            newBrowseName);
                    }
                }
            }
        }
        finally
        {
            _structureLock.Release();
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
            // Find parent subject's node to determine where to attach the new node
            var parentSubject = property.Parent.Subject;
            NodeId parentNodeId;
            string parentPath;

            if (ReferenceEquals(parentSubject, _subject))
            {
                // Parent is the root subject
                if (_configuration.RootName is not null)
                {
                    parentNodeId = new NodeId(_configuration.RootName, NamespaceIndex);
                    parentPath = _configuration.RootName + PathDelimiter;
                }
                else
                {
                    parentNodeId = ObjectIds.ObjectsFolder;
                    parentPath = string.Empty;
                }
            }
            else if (parentSubject is not null && _subjectRefCounter.TryGetData(parentSubject, out var parentNode) && parentNode is not null)
            {
                // Parent subject has an existing node
                parentNodeId = parentNode.NodeId;
                // Extract path from NodeId if it's a string identifier
                parentPath = parentNode.NodeId.Identifier is string stringId
                    ? stringId + PathDelimiter
                    : string.Empty;
            }
            else
            {
                // Parent node not found - can't create child node
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
    public (IInterceptorSubject? Subject, NodeState? Node) AddSubjectFromExternal(
        NodeId typeDefinitionId,
        QualifiedName browseName,
        NodeId parentNodeId)
    {
        if (!_externalNodeManagementHelper.IsEnabled)
        {
            _logger.LogWarning("External AddNode rejected: EnableExternalNodeManagement is disabled.");
            return (null, null);
        }

        var typeRegistry = _configuration.TypeRegistry;
        if (typeRegistry is null)
        {
            _logger.LogWarning("External AddNode rejected: TypeRegistry not configured.");
            return (null, null);
        }

        var csharpType = typeRegistry.ResolveType(typeDefinitionId);
        if (csharpType is null)
        {
            _logger.LogWarning(
                "External AddNode: TypeDefinition '{TypeDefinition}' not registered.",
                typeDefinitionId);
            return (null, null);
        }

        IInterceptorSubject? subject;
        RegisteredSubjectProperty? property;
        object? index;

        _structureLock.Wait();
        try
        {
            // Create the subject instance
            subject = CreateSubjectFromExternalNode(csharpType);
            if (subject is null)
            {
                _logger.LogWarning(
                    "External AddNode: Failed to create subject of type '{Type}'.",
                    csharpType.Name);
                return (null, null);
            }

            // Find the parent property and add the subject to it
            var addResult = TryAddSubjectToParent(subject, parentNodeId, browseName);
            if (addResult.Property is null)
            {
                _logger.LogWarning(
                    "External AddNode: Failed to add subject to parent node '{ParentNodeId}'.",
                    parentNodeId);
                return (null, null);
            }

            property = addResult.Property;
            index = addResult.Index;
        }
        finally
        {
            _structureLock.Release();
        }

        // Create the OPC UA node synchronously (outside the lock to avoid deadlock)
        // CreateSubjectNode acquires _structureLock internally
        CreateSubjectNode(property, subject, index);

        // Get the node that was created for this subject
        if (_subjectRefCounter.TryGetData(subject, out var nodeState) && nodeState is not null)
        {
            // Track externally added subjects for later DeleteNode handling
            lock (_externallyAddedSubjectsLock)
            {
                _externallyAddedSubjects[nodeState.NodeId] = subject;
            }

            _logger.LogInformation(
                "External AddNode: Created subject of type '{Type}' with node '{NodeId}'.",
                csharpType.Name, nodeState.NodeId);

            return (subject, nodeState);
        }

        return (subject, null);
    }

    /// <summary>
    /// Removes a subject based on its NodeId from an external DeleteNodes request.
    /// </summary>
    /// <param name="nodeId">The NodeId of the node to delete.</param>
    /// <returns>True if the subject was found and removed, false otherwise.</returns>
    public bool RemoveSubjectFromExternal(NodeId nodeId)
    {
        if (!_externalNodeManagementHelper.IsEnabled)
        {
            _logger.LogWarning("External DeleteNode rejected: EnableExternalNodeManagement is disabled.");
            return false;
        }

        _structureLock.Wait();
        try
        {
            // Check if this node was externally added
            IInterceptorSubject? externalSubject = null;
            lock (_externallyAddedSubjectsLock)
            {
                _externallyAddedSubjects.TryGetValue(nodeId, out externalSubject);
            }

            if (externalSubject is null)
            {
                // Try to find the subject by its node
                externalSubject = FindSubjectByNodeId(nodeId);
            }

            if (externalSubject is null)
            {
                _logger.LogWarning(
                    "External DeleteNode: No subject found for node '{NodeId}'.",
                    nodeId);
                return false;
            }

            // Remove the subject from the C# model
            RemoveSubjectFromModel(externalSubject);

            // Clean up tracking
            lock (_externallyAddedSubjectsLock)
            {
                _externallyAddedSubjects.Remove(nodeId);
            }

            _logger.LogDebug(
                "External DeleteNode: Removed subject for node '{NodeId}'.",
                nodeId);

            return true;
        }
        finally
        {
            _structureLock.Release();
        }
    }

    /// <summary>
    /// Finds a subject by its OPC UA NodeId.
    /// </summary>
    private IInterceptorSubject? FindSubjectByNodeId(NodeId nodeId)
    {
        foreach (var (subject, nodeState) in _subjectRefCounter.GetAllEntries())
        {
            if (nodeState.NodeId.Equals(nodeId))
            {
                return subject;
            }
        }
        return null;
    }

    /// <summary>
    /// Tries to add a subject to its parent property based on the parent node.
    /// Returns the property and index used for adding, or (null, null) on failure.
    /// </summary>
    private (RegisteredSubjectProperty? Property, object? Index) TryAddSubjectToParent(IInterceptorSubject subject, NodeId parentNodeId, QualifiedName browseName)
    {
        // Find the parent subject
        IInterceptorSubject? parentSubject = null;
        string? containerPropertyName = null;

        // Check if parent is the root
        if (_configuration.RootName is not null)
        {
            var rootNodeId = new NodeId(_configuration.RootName, NamespaceIndex);
            if (parentNodeId.Equals(rootNodeId))
            {
                parentSubject = _subject;
            }
        }
        else if (parentNodeId.Equals(ObjectIds.ObjectsFolder))
        {
            parentSubject = _subject;
        }

        // Try to find parent subject from ref counter
        if (parentSubject is null)
        {
            foreach (var (subj, nodeState) in _subjectRefCounter.GetAllEntries())
            {
                if (nodeState.NodeId.Equals(parentNodeId))
                {
                    parentSubject = subj;
                    break;
                }
            }
        }

        // If not found, check if parentNodeId is a container node (FolderType)
        // Container nodes are created for collections/dictionaries and their parent is the actual subject
        if (parentSubject is null)
        {
            var containerNode = FindNodeInAddressSpace(parentNodeId);
            if (containerNode is FolderState folderState)
            {
                // Container node found - get the container's browse name (this is the property name)
                containerPropertyName = folderState.BrowseName?.Name;

                // Find the parent of the container by parsing the path-based NodeId
                // Container node IDs are path-based like "Root.PropertyName" - the parent is "Root"
                if (containerNode.NodeId.IdType == IdType.String &&
                    containerNode.NodeId.Identifier is string nodePath &&
                    !string.IsNullOrEmpty(nodePath))
                {
                    var lastDotIndex = nodePath.LastIndexOf('.');
                    if (lastDotIndex > 0)
                    {
                        var containerParentId = new NodeId(nodePath.Substring(0, lastDotIndex), containerNode.NodeId.NamespaceIndex);

                        // Check if container's parent is root
                        if (_configuration.RootName is not null)
                        {
                            var rootNodeId = new NodeId(_configuration.RootName, NamespaceIndex);
                            if (containerParentId.Equals(rootNodeId))
                            {
                                parentSubject = _subject;
                            }
                        }
                        else if (containerParentId.Equals(ObjectIds.ObjectsFolder))
                        {
                            parentSubject = _subject;
                        }

                        // Try to find container's parent in ref counter
                        if (parentSubject is null)
                        {
                            foreach (var (subj, nodeState) in _subjectRefCounter.GetAllEntries())
                            {
                                if (nodeState.NodeId.Equals(containerParentId))
                                {
                                    parentSubject = subj;
                                    break;
                                }
                            }
                        }
                    }
                }
            }
        }

        if (parentSubject is null)
        {
            _logger.LogWarning(
                "External AddNode: Could not find parent subject for node '{ParentNodeId}'.",
                parentNodeId);
            return (null, null);
        }

        // Find a suitable collection or reference property on the parent
        var registeredParent = parentSubject.TryGetRegisteredSubject();
        if (registeredParent is null)
        {
            return (null, null);
        }

        foreach (var property in registeredParent.Properties)
        {
            // If we know the container property name, skip properties that don't match
            var propertyName = property.ResolvePropertyName(_configuration.NodeMapper);
            if (containerPropertyName is not null && propertyName != containerPropertyName)
            {
                continue;
            }

            // Check if this property can accept the subject type
            if (property.IsSubjectCollection)
            {
                // For Flat mode collections, the browse name should match pattern "PropertyName[index]"
                // Check if this browse name matches this property's pattern
                var nodeConfiguration = _nodeMapper.TryGetNodeConfiguration(property);
                var collectionStructure = nodeConfiguration?.CollectionStructure ?? Attributes.CollectionNodeStructure.Container;

                if (containerPropertyName is null && collectionStructure == Attributes.CollectionNodeStructure.Flat)
                {
                    // In Flat mode, verify the browse name matches this property's pattern
                    if (!Namotion.Interceptor.OpcUa.Graph.OpcUaBrowseHelper.TryParseCollectionIndex(browseName.Name, propertyName, out _))
                    {
                        continue; // Browse name doesn't match this property's pattern
                    }
                }

                var elementType = GetCollectionElementType(property.Type);
                if (elementType is not null && elementType.IsAssignableFrom(subject.GetType()))
                {
                    var currentValue = property.GetValue();

                    // Handle arrays specially - they're fixed size, need to create new array
                    if (property.Type.IsArray && currentValue is Array array)
                    {
                        var addedIndex = array.Length;
                        var newArray = Array.CreateInstance(elementType, array.Length + 1);
                        Array.Copy(array, newArray, array.Length);
                        newArray.SetValue(subject, addedIndex);
                        property.SetValue(newArray);
                        _logger.LogDebug(
                            "External AddNode: Added subject to array property '{PropertyName}' at index {Index}.",
                            property.Name, addedIndex);
                        return (property, addedIndex);
                    }

                    if (currentValue is System.Collections.IList list)
                    {
                        var addedIndex = list.Count;
                        list.Add(subject);
                        _logger.LogDebug(
                            "External AddNode: Added subject to collection property '{PropertyName}' at index {Index}.",
                            property.Name, addedIndex);
                        return (property, addedIndex);
                    }
                }
            }
            else if (property.IsSubjectDictionary)
            {
                var valueType = GetDictionaryValueType(property.Type);
                if (valueType is not null && valueType.IsAssignableFrom(subject.GetType()))
                {
                    var currentValue = property.GetValue();
                    if (currentValue is System.Collections.IDictionary dict)
                    {
                        // Use BrowseName as the dictionary key
                        var key = browseName.Name;
                        dict[key] = subject;
                        _logger.LogDebug(
                            "External AddNode: Added subject to dictionary property '{PropertyName}' with key '{Key}'.",
                            property.Name, key);
                        return (property, key);
                    }
                }
            }
            else if (property.IsSubjectReference && property.GetValue() is null)
            {
                // For reference properties, the browse name must match the property name exactly
                if (containerPropertyName is null && browseName.Name != propertyName)
                {
                    continue; // Browse name doesn't match this property
                }

                var propertyType = property.Type;
                if (propertyType.IsAssignableFrom(subject.GetType()))
                {
                    property.SetValue(subject);
                    _logger.LogDebug(
                        "External AddNode: Set reference property '{PropertyName}' to subject.",
                        property.Name);
                    return (property, null);
                }
            }
        }

        _logger.LogWarning(
            "External AddNode: No suitable property found on parent to accept subject of type '{Type}'.",
            subject.GetType().Name);
        return (null, null);
    }

    private static Type? GetCollectionElementType(Type collectionType)
    {
        if (collectionType.IsArray)
        {
            return collectionType.GetElementType();
        }

        if (collectionType.IsGenericType)
        {
            var genericArgs = collectionType.GetGenericArguments();
            if (genericArgs.Length == 1)
            {
                return genericArgs[0];
            }
        }

        return null;
    }

    private static Type? GetDictionaryValueType(Type dictionaryType)
    {
        if (dictionaryType.IsGenericType)
        {
            var genericArgs = dictionaryType.GetGenericArguments();
            if (genericArgs.Length == 2)
            {
                return genericArgs[1];
            }
        }

        return null;
    }

    /// <summary>
    /// Creates a subject instance from a C# type using the subject's context.
    /// </summary>
    private IInterceptorSubject? CreateSubjectFromExternalNode(Type csharpType)
    {
        try
        {
            // Create a new subject instance using the shared context
            var constructor = csharpType.GetConstructor([typeof(IInterceptorSubjectContext)]);
            if (constructor is not null)
            {
                var subject = (IInterceptorSubject)constructor.Invoke([_subject.Context]);
                return subject;
            }

            // Try parameterless constructor
            constructor = csharpType.GetConstructor(Type.EmptyTypes);
            if (constructor is not null)
            {
                _logger.LogWarning(
                    "External AddNode: Type '{Type}' only has parameterless constructor. " +
                    "Subject will not be tracked by the interceptor context.",
                    csharpType.Name);
                return (IInterceptorSubject)constructor.Invoke(null);
            }

            _logger.LogError(
                "External AddNode: Type '{Type}' has no suitable constructor.",
                csharpType.Name);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "External AddNode: Failed to create instance of type '{Type}'.",
                csharpType.Name);
            return null;
        }
    }

    /// <summary>
    /// Removes a subject from the C# model by detaching it from its parent property.
    /// </summary>
    private void RemoveSubjectFromModel(IInterceptorSubject subject)
    {
        try
        {
            // Find the property that references this subject and remove the reference
            var registeredSubject = subject.TryGetRegisteredSubject();
            if (registeredSubject is null)
            {
                return;
            }

            // A subject can have multiple parents - process all of them
            foreach (var parent in registeredSubject.Parents)
            {
                var parentProperty = parent.Property;

                if (parentProperty.IsSubjectCollection)
                {
                    // Remove from collection - create new array/list without the subject
                    var currentValue = parentProperty.GetValue();
                    if (currentValue is System.Collections.IList list)
                    {
                        // For arrays, we need to create a new array without the subject
                        // because arrays have fixed size and Remove throws NotSupportedException
                        var elementType = list.GetType().GetElementType() ??
                            (list.GetType().IsGenericType ? list.GetType().GetGenericArguments()[0] : typeof(object));

                        var newList = new List<object?>();
                        foreach (var item in list)
                        {
                            if (!ReferenceEquals(item, subject))
                            {
                                newList.Add(item);
                            }
                        }

                        // Create a new array of the same type
                        var newArray = Array.CreateInstance(elementType, newList.Count);
                        for (var i = 0; i < newList.Count; i++)
                        {
                            newArray.SetValue(newList[i], i);
                        }

                        parentProperty.SetValue(newArray);
                        _logger.LogDebug(
                            "External DeleteNode: Removed subject from collection property '{PropertyName}'. Count: {OldCount} -> {NewCount}.",
                            parentProperty.Name, list.Count, newArray.Length);
                    }
                }
                else if (parentProperty.IsSubjectDictionary)
                {
                    // Remove from dictionary using the tracked index
                    // Must create a new dictionary and call SetValue to trigger change tracking
                    var currentValue = parentProperty.GetValue();
                    if (currentValue is System.Collections.IDictionary dict && parent.Index is not null)
                    {
                        var dictType = currentValue.GetType();
                        if (dictType.IsGenericType && dictType.GetGenericTypeDefinition() == typeof(Dictionary<,>))
                        {
                            // Create new dictionary without the removed key
                            var keyType = dictType.GetGenericArguments()[0];
                            var valueType = dictType.GetGenericArguments()[1];
                            var newDictType = typeof(Dictionary<,>).MakeGenericType(keyType, valueType);
                            var newDict = Activator.CreateInstance(newDictType) as System.Collections.IDictionary;

                            if (newDict is not null)
                            {
                                var oldCount = dict.Count;
                                foreach (System.Collections.DictionaryEntry entry in dict)
                                {
                                    if (!Equals(entry.Key, parent.Index))
                                    {
                                        newDict[entry.Key] = entry.Value;
                                    }
                                }

                                parentProperty.SetValue(newDict);
                                _logger.LogDebug(
                                    "External DeleteNode: Removed subject from dictionary property '{PropertyName}' with key '{Key}'. Count: {OldCount} -> {NewCount}.",
                                    parentProperty.Name, parent.Index, oldCount, newDict.Count);
                            }
                        }
                        else
                        {
                            // Fallback for non-generic dictionaries - modify in-place
                            dict.Remove(parent.Index);
                            _logger.LogDebug(
                                "External DeleteNode: Removed subject from dictionary property '{PropertyName}' with key '{Key}' (in-place).",
                                parentProperty.Name, parent.Index);
                        }
                    }
                }
                else if (parentProperty.IsSubjectReference)
                {
                    // Set single reference to null
                    parentProperty.SetValue(null);
                    _logger.LogDebug(
                        "External DeleteNode: Set reference property '{PropertyName}' to null.",
                        parentProperty.Name);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "External DeleteNode: Error removing subject from model.");
        }
    }

    /// <summary>
    /// Gets whether external node management is enabled for this server.
    /// </summary>
    public bool IsExternalNodeManagementEnabled => _externalNodeManagementHelper.IsEnabled;

    #endregion
}