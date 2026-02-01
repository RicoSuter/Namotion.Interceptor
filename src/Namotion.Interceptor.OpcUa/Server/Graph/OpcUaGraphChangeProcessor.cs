using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Attributes;
using Namotion.Interceptor.OpcUa.Mapping;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Server.Graph;

/// <summary>
/// Processes external node management requests (AddNodes/DeleteNodes) from OPC UA clients.
/// Handles the OPC UA -> Model direction for the server side.
/// Symmetric with Client/Graph/OpcUaGraphChangeProcessor which handles OPC UA -> Model for clients.
/// </summary>
internal class OpcUaGraphChangeProcessor
{
    private readonly IInterceptorSubject _rootSubject;
    private readonly OpcUaServerConfiguration _configuration;
    private readonly IOpcUaNodeMapper _nodeMapper;
    private readonly ConnectorReferenceCounter<NodeState> _subjectRefCounter;
    private readonly ExternalNodeManagementHelper _externalNodeManagementHelper;
    private readonly Func<NodeId, NodeState?> _findNode;
    private readonly Action<RegisteredSubjectProperty, IInterceptorSubject, object?> _createSubjectNode;
    private readonly Func<ushort> _getNamespaceIndex;
    private readonly ILogger _logger;

    // Mapping from NodeId to externally added subjects
    private readonly Dictionary<NodeId, IInterceptorSubject> _externallyAddedSubjects = new();
    private readonly Lock _externallyAddedSubjectsLock = new();

    public OpcUaGraphChangeProcessor(
        IInterceptorSubject rootSubject,
        OpcUaServerConfiguration configuration,
        ConnectorReferenceCounter<NodeState> subjectRefCounter,
        ExternalNodeManagementHelper externalNodeManagementHelper,
        Func<NodeId, NodeState?> findNode,
        Action<RegisteredSubjectProperty, IInterceptorSubject, object?> createSubjectNode,
        Func<ushort> getNamespaceIndex,
        ILogger logger)
    {
        _rootSubject = rootSubject;
        _configuration = configuration;
        _nodeMapper = configuration.NodeMapper;
        _subjectRefCounter = subjectRefCounter;
        _externalNodeManagementHelper = externalNodeManagementHelper;
        _findNode = findNode;
        _createSubjectNode = createSubjectNode;
        _getNamespaceIndex = getNamespaceIndex;
        _logger = logger;
    }

    /// <summary>
    /// Gets whether external node management is enabled for this server.
    /// </summary>
    public bool IsEnabled => _externalNodeManagementHelper.IsEnabled;

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

        // Create the subject instance
        var subject = CreateSubjectFromExternalNode(csharpType);
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

        var property = addResult.Property;
        var index = addResult.Index;

        // Create the OPC UA node
        _createSubjectNode(property, subject, index);

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

    /// <summary>
    /// Finds a subject by its OPC UA NodeId.
    /// </summary>
    public IInterceptorSubject? FindSubjectByNodeId(NodeId nodeId)
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
        var namespaceIndex = _getNamespaceIndex();

        // Find the parent subject
        IInterceptorSubject? parentSubject = null;
        string? containerPropertyName = null;

        // Check if parent is the root
        if (_configuration.RootName is not null)
        {
            var rootNodeId = new NodeId(_configuration.RootName, namespaceIndex);
            if (parentNodeId.Equals(rootNodeId))
            {
                parentSubject = _rootSubject;
            }
        }
        else if (parentNodeId.Equals(ObjectIds.ObjectsFolder))
        {
            parentSubject = _rootSubject;
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
            var containerNode = _findNode(parentNodeId);
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
                            var rootNodeId = new NodeId(_configuration.RootName, namespaceIndex);
                            if (containerParentId.Equals(rootNodeId))
                            {
                                parentSubject = _rootSubject;
                            }
                        }
                        else if (containerParentId.Equals(ObjectIds.ObjectsFolder))
                        {
                            parentSubject = _rootSubject;
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
                var collectionStructure = nodeConfiguration?.CollectionStructure ?? CollectionNodeStructure.Container;

                if (containerPropertyName is null && collectionStructure == CollectionNodeStructure.Flat)
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
                var subject = (IInterceptorSubject)constructor.Invoke([_rootSubject.Context]);
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
}
