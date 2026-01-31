using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
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
    /// </summary>
    public void RemoveSubjectNodes(IInterceptorSubject subject)
    {
        _structureLock.Wait();
        try
        {
            // Decrement reference count and check if this was the last reference
            var isLast = _subjectRefCounter.DecrementAndCheckLast(subject, out var nodeState);

            _logger.LogInformation("RemoveSubjectNodes: subject={SubjectType}, isLast={IsLast}, hasNodeState={HasNodeState}, nodeId={NodeId}",
                subject.GetType().Name, isLast, nodeState is not null, nodeState?.NodeId?.ToString() ?? "null");

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
                RemoveNodeAndReferences(nodeState);

                // Queue model change event for node deletion
                _modelChangePublisher.QueueChange(nodeState.NodeId, ModelChangeStructureVerbMask.NodeDeleted);
            }
        }
        finally
        {
            _structureLock.Release();
        }
    }

    /// <summary>
    /// Removes a node and all references pointing to it from parent nodes.
    /// This is necessary because the OPC UA SDK's DeleteNode only removes the node itself,
    /// but leaves references from parent nodes intact, causing browse operations to still return the deleted node.
    /// </summary>
    /// <param name="nodeState">The node to remove.</param>
    private void RemoveNodeAndReferences(NodeState nodeState)
    {
        var nodeId = nodeState.NodeId;

        // Find parent node by parsing the path-based NodeId
        // Node IDs follow pattern like "Root.People[1]", parent would be "Root.People"
        var parentNodeId = FindParentNodeIdFromPath(nodeId);
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
    /// Finds the parent NodeId by parsing a path-based node identifier.
    /// For "Root.People[1]", returns "Root.People".
    /// For "Root.People[1].Address", returns "Root.People[1]".
    /// </summary>
    private NodeId? FindParentNodeIdFromPath(NodeId nodeId)
    {
        if (nodeId.IdType != IdType.String || nodeId.Identifier is not string path)
        {
            return null;
        }

        // Handle collection item patterns like "Root.People[1]"
        // Parent is the container "Root.People"
        var bracketIndex = path.LastIndexOf('[');
        if (bracketIndex > 0)
        {
            // Check if this is a direct collection item (no dot after the bracket would be in a sub-property)
            var dotAfterBracket = path.IndexOf('.', bracketIndex);
            if (dotAfterBracket < 0)
            {
                // This is a collection item like "Root.People[1]"
                // Parent is "Root.People"
                var containerPath = path.Substring(0, bracketIndex);
                return new NodeId(containerPath, nodeId.NamespaceIndex);
            }
        }

        // For regular path patterns, find the last dot
        var lastDotIndex = path.LastIndexOf('.');
        if (lastDotIndex > 0)
        {
            var parentPath = path.Substring(0, lastDotIndex);
            return new NodeId(parentPath, nodeId.NamespaceIndex);
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
                var containerNode = _nodeCreator.GetOrCreateContainerNode(propertyName, nodeConfiguration, parentNodeId, parentPath);
                var childIndex = index ?? property.Children.Length - 1;
                _nodeCreator.CreateCollectionChildNode(property, subject, childIndex, propertyName, parentPath, containerNode.NodeId, nodeConfiguration);
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

                // Find the parent of the container (should be the actual subject)
                var containerParentId = FindParentNodeId(containerNode);
                if (containerParentId is not null)
                {
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

    /// <summary>
    /// Finds the parent node ID of a container node by parsing its path-based node ID.
    /// Container nodes have node IDs like "Root.PropertyName" - the parent is "Root".
    /// </summary>
    private NodeId? FindParentNodeId(NodeState node)
    {
        // Container node IDs are path-based like "Root.PropertyName"
        // We need to find the parent by removing the last segment
        if (node.NodeId.IdType != IdType.String)
        {
            return null;
        }

        var nodePath = node.NodeId.Identifier as string;
        if (string.IsNullOrEmpty(nodePath))
        {
            return null;
        }

        // Find the last dot to get the parent path
        var lastDotIndex = nodePath.LastIndexOf('.');
        if (lastDotIndex <= 0)
        {
            // No parent path segment - might be root
            return null;
        }

        var parentPath = nodePath.Substring(0, lastDotIndex);
        return new NodeId(parentPath, node.NodeId.NamespaceIndex);
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

    /// <summary>
    /// Gets the external node management helper for validation operations.
    /// </summary>
    internal ExternalNodeManagementHelper ExternalNodeManagementHelper => _externalNodeManagementHelper;

    #endregion
}