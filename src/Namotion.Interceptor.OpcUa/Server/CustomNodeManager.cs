using System.Collections;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Attributes;
using Namotion.Interceptor.OpcUa.Mapping;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Opc.Ua;
using Opc.Ua.Server;

namespace Namotion.Interceptor.OpcUa.Server;

internal class CustomNodeManager : CustomNodeManager2
{
    private const string PathDelimiter = ".";
    internal string SubjectNodeIdDataKey { get; } = "OpcUa:ServerNodeId:" + Guid.NewGuid();

    private readonly IInterceptorSubject _subject;
    private readonly OpcUaSubjectServer _serverService;
    private readonly OpcUaServerConfiguration _configuration;
    private readonly IOpcUaNodeMapper _nodeMapper;
    private readonly ILogger _logger;
    private readonly OpcUaNodeFactory _nodeFactory;

    private readonly SemaphoreSlim _structureLock = new(1, 1);
    private readonly Dictionary<RegisteredSubject, NodeState> _subjects = new();
    private long _dynamicNodeCounter;

    public CustomNodeManager(
        IInterceptorSubject subject,
        OpcUaSubjectServer serverService,
        IServerInternal server,
        ApplicationConfiguration applicationConfiguration,
        OpcUaServerConfiguration configuration,
        ILogger logger) :
        base(server, applicationConfiguration, configuration.GetNamespaceUris())
    {
        _subject = subject;
        _serverService = serverService;
        _configuration = configuration;
        _nodeMapper = configuration.NodeMapper;
        _logger = logger;
        _nodeFactory = new OpcUaNodeFactory(logger);
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
                property.Reference.RemovePropertyData(_serverService.OpcUaVariableKey);
                ClearAttributePropertyData(property);
            }
        }

        foreach (var subject in _subjects.Keys)
        {
            foreach (var property in subject.Properties)
            {
                property.Reference.RemovePropertyData(_serverService.OpcUaVariableKey);
                ClearAttributePropertyData(property);
            }
        }
    }

    private void ClearAttributePropertyData(RegisteredSubjectProperty property)
    {
        foreach (var attribute in property.Attributes)
        {
            attribute.Reference.RemovePropertyData(_serverService.OpcUaVariableKey);
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

                    CreateSubjectNodes(node.NodeId, registeredSubject, _configuration.RootName + PathDelimiter);
                }
                else
                {
                    CreateSubjectNodes(ObjectIds.ObjectsFolder, registeredSubject, string.Empty);
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
    /// </summary>
    public void RemoveSubjectNodes(IInterceptorSubject subject)
    {
        _structureLock.Wait();
        try
        {
            var registeredSubject = subject.TryGetRegisteredSubject();

            // Remove variable nodes for this subject's properties
            if (registeredSubject != null)
            {
                foreach (var property in registeredSubject.Properties)
                {
                    if (property.Reference.TryGetPropertyData(_serverService.OpcUaVariableKey, out var node)
                        && node is BaseDataVariableState variableNode)
                    {
                        DeleteNode(SystemContext, variableNode.NodeId);
                        property.Reference.RemovePropertyData(_serverService.OpcUaVariableKey);
                    }
                }
            }

            // Remove object nodes
            var keysToRemove = _subjects.Where(kvp => kvp.Key.Subject == subject).Select(kvp => kvp.Key).ToList();
            foreach (var key in keysToRemove)
            {
                if (_subjects.TryGetValue(key, out var nodeState))
                {
                    DeleteNode(SystemContext, nodeState.NodeId);
                    _subjects.Remove(key);
                }
            }
        }
        finally
        {
            _structureLock.Release();
        }
    }

    /// <summary>
    /// Re-maps the internal subject dictionary from an old (detached) subject to a new subject,
    /// keeping the same OPC UA object node. Used for same-path reference replacement.
    /// </summary>
    /// <returns>True if the old subject was found and replaced, false otherwise.</returns>
    public bool TryReplaceSubjectMapping(
        IInterceptorSubject oldSubject,
        IInterceptorSubject newSubject)
    {
        var newRegistered = newSubject.TryGetRegisteredSubject();
        if (newRegistered is null)
        {
            return false;
        }

        _structureLock.Wait();
        try
        {
            // Find the old subject's entry in _subjects
            RegisteredSubject? oldKey = null;
            NodeState? existingNode = null;
            foreach (var kvp in _subjects)
            {
                if (kvp.Key.Subject == oldSubject)
                {
                    oldKey = kvp.Key;
                    existingNode = kvp.Value;
                    break;
                }
            }

            if (oldKey is null || existingNode is null)
            {
                return false;
            }

            // Re-map: remove old key, add new key pointing to same node
            _subjects.Remove(oldKey);
            _subjects[newRegistered] = existingNode;

            return true;
        }
        finally
        {
            _structureLock.Release();
        }
    }

    private void CreateSubjectNodes(NodeId parentNodeId, RegisteredSubject subject, string prefix)
    {
        foreach (var property in subject.Properties)
        {
            if (property.IsAttribute)
                continue;

            var propertyName = property.ResolvePropertyName(_nodeMapper);
            if (propertyName is not null)
            {
                if (property.IsSubjectCollection)
                {
                    CreateArrayObjectNode(propertyName, property, property.Children, parentNodeId, prefix);
                }
                else if (property.IsSubjectDictionary)
                {
                    CreateDictionaryObjectNode(propertyName, property, property.Children, parentNodeId, prefix);
                }
                else if (property.IsSubjectReference)
                {
                    var referencedSubject = property.Children.SingleOrDefault();
                    if (referencedSubject.Subject is not null)
                    {
                        // Check if this should be a VariableNode instead of ObjectNode
                        var nodeConfiguration = _nodeMapper.TryGetNodeConfiguration(property);
                        if (nodeConfiguration?.NodeClass == OpcUaNodeClass.Variable)
                        {
                            CreateVariableNodeForSubject(propertyName, property, parentNodeId, prefix);
                        }
                        else
                        {
                            CreateReferenceObjectNode(propertyName, property, referencedSubject, parentNodeId, prefix);
                        }
                    }
                }
                else 
                {
                    CreateVariableNode(propertyName, property, parentNodeId, prefix);
                }
            }
        }
    }

    private void CreateReferenceObjectNode(string propertyName, RegisteredSubjectProperty property, SubjectPropertyChild child, NodeId parentNodeId, string parentPath)
    {
        var path = parentPath + propertyName;
        var nodeConfiguration = _nodeMapper.TryGetNodeConfiguration(property);
        var browseName = _nodeFactory.GetBrowseName(this, propertyName, nodeConfiguration, child.Index);
        var referenceTypeId = _nodeFactory.GetReferenceTypeId(this, nodeConfiguration);

        CreateChildObject(property, browseName, child.Subject, path, parentNodeId, referenceTypeId);
    }

    private void CreateArrayObjectNode(string propertyName, RegisteredSubjectProperty property, ICollection<SubjectPropertyChild> children, NodeId parentNodeId, string parentPath)
    {
        // Cache node configuration to avoid repeated lookups
        var nodeConfiguration = _nodeMapper.TryGetNodeConfiguration(property);

        var nodeId = _nodeFactory.GetNodeId(this, nodeConfiguration, parentPath + propertyName);
        var browseName = _nodeFactory.GetBrowseName(this, propertyName, nodeConfiguration, null);

        var typeDefinitionId = _nodeFactory.GetTypeDefinitionId(this, nodeConfiguration);
        var referenceTypeId = _nodeFactory.GetReferenceTypeId(this, nodeConfiguration);

        var propertyNode = _nodeFactory.CreateFolderNode(this, parentNodeId, nodeId, browseName, typeDefinitionId, referenceTypeId, nodeConfiguration);

        var childReferenceTypeId = _nodeFactory.GetChildReferenceTypeId(this, nodeConfiguration);
        foreach (var child in children)
        {
            var childBrowseName = new QualifiedName($"{propertyName}[{child.Index}]", NamespaceIndex);
            var counter = Interlocked.Increment(ref _dynamicNodeCounter);
            var childPath = $"{parentPath}{propertyName}_{counter}";

            CreateChildObject(property, childBrowseName, child.Subject, childPath, propertyNode.NodeId, childReferenceTypeId);
        }
    }

    private void CreateDictionaryObjectNode(string propertyName, RegisteredSubjectProperty property, ICollection<SubjectPropertyChild> children, NodeId parentNodeId, string parentPath)
    {
        // Cache node configuration to avoid repeated lookups
        var nodeConfiguration = _nodeMapper.TryGetNodeConfiguration(property);

        var nodeId = _nodeFactory.GetNodeId(this, nodeConfiguration, parentPath + propertyName);
        var browseName = _nodeFactory.GetBrowseName(this, propertyName, nodeConfiguration, null);

        var typeDefinitionId = _nodeFactory.GetTypeDefinitionId(this, nodeConfiguration);
        var referenceTypeId = _nodeFactory.GetReferenceTypeId(this, nodeConfiguration);

        var propertyNode = _nodeFactory.CreateFolderNode(this, parentNodeId, nodeId, browseName, typeDefinitionId, referenceTypeId, nodeConfiguration);
        var childReferenceTypeId = _nodeFactory.GetChildReferenceTypeId(this, nodeConfiguration);
        foreach (var child in children)
        {
            var indexString = child.Index?.ToString();
            if (string.IsNullOrEmpty(indexString))
            {
                _logger.LogWarning(
                    "Dictionary property '{PropertyName}' contains a child with null or empty key. Skipping OPC UA node creation.",
                    propertyName);

                continue;
            }

            var childBrowseName = new QualifiedName(indexString, NamespaceIndex);
            var counter = Interlocked.Increment(ref _dynamicNodeCounter);
            var childPath = $"{parentPath}{propertyName}_{counter}";

            CreateChildObject(property, childBrowseName, child.Subject, childPath, propertyNode.NodeId, childReferenceTypeId);
        }
    }

    private BaseDataVariableState CreateVariableNode(
        string propertyName,
        RegisteredSubjectProperty property,
        NodeId parentNodeId,
        string parentPath,
        RegisteredSubjectProperty? configurationProperty = null)
    {
        // Use configurationProperty for node identity, property for value/type
        var actualConfigurationProperty = configurationProperty ?? property;

        var nodeConfiguration = _nodeMapper.TryGetNodeConfiguration(actualConfigurationProperty);
        var nodeId = _nodeFactory.GetNodeId(this, nodeConfiguration, parentPath + propertyName);
        var browseName = _nodeFactory.GetBrowseName(this, propertyName, nodeConfiguration, null);
        var referenceTypeId = _nodeFactory.GetReferenceTypeId(this, nodeConfiguration);
        var dataTypeOverride = _nodeFactory.GetDataTypeOverride(this, _nodeMapper.TryGetNodeConfiguration(property));

        var variableNode = ConfigureVariableNode(property, parentNodeId, nodeId, browseName, referenceTypeId, dataTypeOverride, nodeConfiguration);

        property.Reference.SetPropertyData(_serverService.OpcUaVariableKey, variableNode);

        CreateAttributeNodes(variableNode, property, parentPath + propertyName);
        return variableNode;
    }

    private void CreateAttributeNodes(NodeState parentNode, RegisteredSubjectProperty property, string parentPath)
    {
        foreach (var attribute in property.Attributes)
        {
            var attributeConfiguration = _nodeMapper.TryGetNodeConfiguration(attribute);
            if (attributeConfiguration is null)
                continue;

            var attributeName = attributeConfiguration.BrowseName ?? attribute.BrowseName;
            var attributePath = parentPath + PathDelimiter + attributeName;
            var referenceTypeId = _nodeFactory.GetReferenceTypeId(this, attributeConfiguration) ?? ReferenceTypeIds.HasProperty;

            // Create variable node for attribute
            var attributeNode = CreateVariableNodeForAttribute(
                attributeName,
                attribute,
                parentNode.NodeId,
                attributePath,
                referenceTypeId);

            // Recursive: attributes can have attributes
            CreateAttributeNodes(attributeNode, attribute, attributePath);
        }
    }

    private BaseDataVariableState CreateVariableNodeForAttribute(
        string attributeName,
        RegisteredSubjectProperty attribute,
        NodeId parentNodeId,
        string path,
        NodeId referenceTypeId)
    {
        var nodeConfiguration = _nodeMapper.TryGetNodeConfiguration(attribute);
        var nodeId = _nodeFactory.GetNodeId(this, nodeConfiguration, path);
        var browseName = _nodeFactory.GetBrowseName(this, attributeName, nodeConfiguration, null);
        var dataTypeOverride = _nodeFactory.GetDataTypeOverride(this, nodeConfiguration);

        var variableNode = ConfigureVariableNode(attribute, parentNodeId, nodeId, browseName, referenceTypeId, dataTypeOverride, nodeConfiguration);

        attribute.Reference.SetPropertyData(_serverService.OpcUaVariableKey, variableNode);

        return variableNode;
    }

    /// <summary>
    /// Shared helper that configures a variable node with value, access levels, array dimensions, and state change handler.
    /// </summary>
    private BaseDataVariableState ConfigureVariableNode(
        RegisteredSubjectProperty property,
        NodeId parentNodeId,
        NodeId nodeId,
        QualifiedName browseName,
        NodeId? referenceTypeId,
        NodeId? dataTypeOverride,
        OpcUaNodeConfiguration? nodeConfiguration)
    {
        var value = _configuration.ValueConverter.ConvertToNodeValue(property.GetValue(), property);
        var typeInfo = _configuration.ValueConverter.GetNodeTypeInfo(property.Type);

        var variableNode = _nodeFactory.CreateVariableNode(this, parentNodeId, nodeId, browseName, typeInfo, referenceTypeId, dataTypeOverride, nodeConfiguration);
        _nodeFactory.AddAdditionalReferences(this, variableNode, nodeConfiguration);
        variableNode.Handle = property.Reference;

        // Adjust access according to property setter
        if (!property.HasSetter)
        {
            variableNode.AccessLevel = AccessLevels.CurrentRead;
            variableNode.UserAccessLevel = AccessLevels.CurrentRead;
        }
        else
        {
            variableNode.AccessLevel = AccessLevels.CurrentReadOrWrite;
            variableNode.UserAccessLevel = AccessLevels.CurrentReadOrWrite;
        }

        // Set array dimensions (works for 1D and multi-D)
        if (value is Array arrayValue)
        {
            variableNode.ArrayDimensions = new ReadOnlyList<uint>(
                Enumerable.Range(0, arrayValue.Rank)
                    .Select(i => (uint)arrayValue.GetLength(i))
                    .ToArray());
        }

        variableNode.Value = value;

        var writeTimestamp = property.Reference.TryGetWriteTimestamp();
        if (writeTimestamp.HasValue)
        {
            variableNode.Timestamp = writeTimestamp.Value.UtcDateTime;
        }

        variableNode.StateChanged += (_, _, changes) =>
        {
            if (changes.HasFlag(NodeStateChangeMasks.Value))
            {
                // No lock needed: StateChanged fires from ClearChangeMasks which is always
                // called under NodeManager.Lock (from WriteChangesAsync or SDK write handling).
                _serverService.UpdateProperty(property.Reference, variableNode.Timestamp, variableNode.Value);
            }
        };

        return variableNode;
    }

    private void CreateVariableNodeForSubject(string propertyName, RegisteredSubjectProperty property, NodeId parentNodeId, string parentPath)
    {
        // Get the child subject - skip if null (structural sync will handle later)
        var childSubject = property.Children.SingleOrDefault().Subject?.TryGetRegisteredSubject();
        if (childSubject is null)
        {
            return;
        }

        // Find the [OpcUaValue] property
        var valueProperty = childSubject.TryGetValueProperty(_nodeMapper);
        if (valueProperty is null)
        {
            return;
        }

        // Create the variable node: value from valueProperty, config from containing property
        var variableNode = CreateVariableNode(propertyName, valueProperty, parentNodeId, parentPath, configurationProperty: property);

        // Create child properties of the VariableNode (excluding the value property)
        var path = parentPath + propertyName;
        foreach (var childProperty in childSubject.Properties)
        {
            var childConfig = _nodeMapper.TryGetNodeConfiguration(childProperty);
            if (childConfig?.IsValue != true)
            {
                var childName = childProperty.ResolvePropertyName(_nodeMapper);
                if (childName != null)
                {
                    CreateVariableNode(childName, childProperty, variableNode.NodeId, path + PathDelimiter);
                }
            }
        }
    }

    private void CreateChildObject(RegisteredSubjectProperty property, QualifiedName browseName,
        IInterceptorSubject subject,
        string path,
        NodeId parentNodeId,
        NodeId? referenceTypeId)
    {
        var registeredSubject = subject.TryGetRegisteredSubject() ?? throw new InvalidOperationException("Registered subject not found.");

        if (_subjects.TryGetValue(registeredSubject, out var existingNode))
        {
            // Subject already created, add reference to existing node
            var parentNode = FindNodeInAddressSpace(parentNodeId);
            parentNode.AddReference(referenceTypeId ?? ReferenceTypeIds.HasComponent, false, existingNode.NodeId);
        }
        else
        {
            // Create new node and add to dictionary (protected by _structureLock)
            var nodeConfiguration = _nodeMapper.TryGetNodeConfiguration(property);
            var nodeId = _nodeFactory.GetNodeId(this, nodeConfiguration, path);
            var typeDefinitionId = GetTypeDefinitionIdForSubject(subject);

            var node = _nodeFactory.CreateObjectNode(this, parentNodeId, nodeId, browseName, typeDefinitionId, referenceTypeId, nodeConfiguration);
            _nodeFactory.AddAdditionalReferences(this, node, nodeConfiguration);
            _subjects[registeredSubject] = node;
            subject.SetData(SubjectNodeIdDataKey, node.NodeId);
            CreateSubjectNodes(node.NodeId, registeredSubject, path + PathDelimiter);
        }
    }

    /// <summary>
    /// Creates OPC UA nodes for a subject that was dynamically attached at runtime.
    /// Returns the created node, or null if the subject could not be added (e.g., parent not found).
    /// </summary>
    public NodeState? CreateDynamicSubjectNodes(SubjectLifecycleChange change)
    {
        if (change.Property is not { } property)
        {
            return null;
        }

        var parentSubject = property.Subject.TryGetRegisteredSubject();
        if (parentSubject is null)
        {
            return null;
        }

        var registeredProperty = property.TryGetRegisteredProperty();
        if (registeredProperty is null)
        {
            return null;
        }

        var registeredSubject = change.Subject.TryGetRegisteredSubject();
        if (registeredSubject is null)
        {
            return null;
        }

        _structureLock.Wait();
        try
        {
            // Already created (dedup)
            if (_subjects.TryGetValue(registeredSubject, out var existing))
            {
                return existing;
            }

            // Find the parent node in the address space
            NodeId? parentNodeId;
            string parentPath;

            if (_subjects.TryGetValue(parentSubject, out var parentNode))
            {
                parentNodeId = parentNode.NodeId;
                // Reconstruct the path prefix from the parent node's NodeId string identifier
                parentPath = parentNode.NodeId.Identifier is string stringId
                    ? stringId + PathDelimiter
                    : string.Empty;
            }
            else if (parentSubject.Subject == _subject)
            {
                // Parent is the root subject
                if (_configuration.RootName is not null)
                {
                    var rootNodeId = new NodeId(_configuration.RootName, NamespaceIndex);
                    parentNodeId = rootNodeId;
                    parentPath = _configuration.RootName + PathDelimiter;
                }
                else
                {
                    parentNodeId = ObjectIds.ObjectsFolder;
                    parentPath = string.Empty;
                }
            }
            else
            {
                return null;
            }

            var propertyName = registeredProperty.ResolvePropertyName(_nodeMapper);
            if (propertyName is null)
            {
                return null;
            }

            // For collections and dictionaries, we need to find or create the container folder
            // and then add the child beneath it
            if (registeredProperty.IsSubjectCollection)
            {
                var folderNodeId = _nodeFactory.GetNodeId(this, _nodeMapper.TryGetNodeConfiguration(registeredProperty), parentPath + propertyName);
                var folderNode = FindNodeInAddressSpace(folderNodeId);
                if (folderNode is null)
                {
                    return null;
                }

                var nodeConfiguration = _nodeMapper.TryGetNodeConfiguration(registeredProperty);
                var childReferenceTypeId = _nodeFactory.GetChildReferenceTypeId(this, nodeConfiguration);
                var childBrowseName = new QualifiedName($"{propertyName}[{change.Index}]", NamespaceIndex);
                var counter = Interlocked.Increment(ref _dynamicNodeCounter);
                var childPath = $"{parentPath}{propertyName}_{counter}";

                CreateChildObject(registeredProperty, childBrowseName, change.Subject, childPath, folderNode.NodeId, childReferenceTypeId);
                return _subjects.GetValueOrDefault(registeredSubject);
            }
            else if (registeredProperty.IsSubjectDictionary)
            {
                var folderNodeId = _nodeFactory.GetNodeId(this, _nodeMapper.TryGetNodeConfiguration(registeredProperty), parentPath + propertyName);
                var folderNode = FindNodeInAddressSpace(folderNodeId);
                if (folderNode is null)
                {
                    return null;
                }

                var indexString = change.Index?.ToString();
                if (string.IsNullOrEmpty(indexString))
                {
                    return null;
                }

                var nodeConfiguration = _nodeMapper.TryGetNodeConfiguration(registeredProperty);
                var childReferenceTypeId = _nodeFactory.GetChildReferenceTypeId(this, nodeConfiguration);
                var childBrowseName = new QualifiedName(indexString, NamespaceIndex);
                var counter = Interlocked.Increment(ref _dynamicNodeCounter);
                var childPath = $"{parentPath}{propertyName}_{counter}";

                CreateChildObject(registeredProperty, childBrowseName, change.Subject, childPath, folderNode.NodeId, childReferenceTypeId);
                return _subjects.GetValueOrDefault(registeredSubject);
            }
            else if (registeredProperty.IsSubjectReference)
            {
                var nodeConfiguration = _nodeMapper.TryGetNodeConfiguration(registeredProperty);
                if (nodeConfiguration?.NodeClass == OpcUaNodeClass.Variable)
                {
                    CreateVariableNodeForSubject(propertyName, registeredProperty, parentNodeId, parentPath);
                }
                else
                {
                    var counter = Interlocked.Increment(ref _dynamicNodeCounter);
                    var childPath = $"{parentPath}{propertyName}_{counter}";
                    var browseName = _nodeFactory.GetBrowseName(this, propertyName, nodeConfiguration, change.Index);
                    var referenceTypeId = _nodeFactory.GetReferenceTypeId(this, nodeConfiguration);
                    CreateChildObject(registeredProperty, browseName, change.Subject, childPath, parentNodeId, referenceTypeId);
                }
                return _subjects.GetValueOrDefault(registeredSubject);
            }

            return null;
        }
        finally
        {
            _structureLock.Release();
        }
    }

    /// <summary>
    /// Gets the NodeId for a registered subject, or null if not found.
    /// </summary>
    public NodeId? TryGetNodeIdForSubject(RegisteredSubject subject)
    {
        if (_subjects.TryGetValue(subject, out var node))
        {
            return node.NodeId;
        }

        // Check root subject
        if (subject.Subject == _subject && _configuration.RootName is not null)
        {
            return new NodeId(_configuration.RootName, NamespaceIndex);
        }

        return null;
    }

    /// <summary>
    /// Tries to get the NodeId for a registered subject from the internal subjects dictionary.
    /// </summary>
    public bool TryGetNodeId(RegisteredSubject subject, out NodeId? nodeId)
    {
        if (_subjects.TryGetValue(subject, out var node))
        {
            nodeId = node.NodeId;
            return true;
        }
        nodeId = null;
        return false;
    }

    /// <summary>
    /// Fires a GeneralModelChangeEvent to notify connected clients of address space structure changes.
    /// </summary>
    public void FireModelChangeEvent(ModelChangeStructureVerbMask verb, NodeId affectedNodeId)
    {
        var context = SystemContext;
        var eventState = new GeneralModelChangeEventState(null);

        eventState.Initialize(
            context,
            source: null,
            EventSeverity.Low,
            new LocalizedText("Address space structure changed"));

        eventState.SetChildValue(context, BrowseNames.SourceNode, ObjectIds.Server, false);
        eventState.SetChildValue(context, BrowseNames.SourceName, "Server", false);
        eventState.SetChildValue(context, BrowseNames.Changes,
            new[]
            {
                new ModelChangeStructureDataType
                {
                    Verb = (byte)verb,
                    Affected = affectedNodeId,
                    AffectedType = ObjectTypeIds.BaseObjectType
                }
            }, false);
        eventState.SetChildValue(context, BrowseNames.Time, DateTime.UtcNow, false);
        eventState.SetChildValue(context, BrowseNames.ReceiveTime, DateTime.UtcNow, false);

        Server.ReportEvent(eventState);
    }

    /// <summary>
    /// Forces data change notifications for all variable nodes belonging to a subject.
    /// Call this after dynamically creating nodes for a subject that reuses NodeIds
    /// (e.g., when a reference property is replaced with a new subject at the same path).
    /// Without this, existing monitored items may not receive the new values because
    /// the node was deleted and recreated at the same NodeId.
    /// </summary>
    public void ClearChangeMasksForSubject(IInterceptorSubject subject)
    {
        var registeredSubject = subject.TryGetRegisteredSubject();
        if (registeredSubject is null)
        {
            return;
        }

        var context = SystemContext;

        foreach (var property in registeredSubject.Properties)
        {
            if (property.Reference.TryGetPropertyData(_serverService.OpcUaVariableKey, out var data) &&
                data is BaseDataVariableState variableNode)
            {
                variableNode.ClearChangeMasks(context, false);
            }

            // Also clear change masks for attributes
            foreach (var attribute in property.Attributes)
            {
                if (attribute.Reference.TryGetPropertyData(_serverService.OpcUaVariableKey, out var attrData) &&
                    attrData is BaseDataVariableState attrNode)
                {
                    attrNode.ClearChangeMasks(context, false);
                }
            }
        }
    }

    private NodeId? GetTypeDefinitionIdForSubject(IInterceptorSubject subject)
    {
        // For subjects, check if type has OpcUaNode attribute at class level
        var typeAttribute = subject.GetType().GetCustomAttribute<OpcUaNodeAttribute>();
        return _nodeFactory.GetTypeDefinitionId(this, typeAttribute);
    }

    /// <summary>
    /// Processes an AddNodes request from a remote client.
    /// Creates a subject in the local model and the corresponding OPC UA nodes.
    /// Called by OpcUaStandardServer.AddNodesAsync when AllowRemoteNodeManagement is enabled.
    /// </summary>
    /// <returns>The result for each AddNodesItem, containing the assigned NodeId or error status.</returns>
    public AddNodesResult HandleRemoteAddNode(AddNodesItem item)
    {
        if (_configuration.SubjectFactory is null)
        {
            return new AddNodesResult
            {
                StatusCode = StatusCodes.BadNotSupported,
                AddedNodeId = NodeId.Null
            };
        }

        IInterceptorSubject newSubject;
        RegisteredSubjectProperty property;
        object? index;

        _structureLock.Wait();
        try
        {
            var parentNodeId = ExpandedNodeId.ToNodeId(item.ParentNodeId, Server.NamespaceUris);
            if (parentNodeId is null)
            {
                return new AddNodesResult
                {
                    StatusCode = StatusCodes.BadParentNodeIdInvalid,
                    AddedNodeId = NodeId.Null
                };
            }

            var (parentRegisteredSubject, _) = FindSubjectByNodeId(parentNodeId);
            if (parentRegisteredSubject is null)
            {
                return new AddNodesResult
                {
                    StatusCode = StatusCodes.BadParentNodeIdInvalid,
                    AddedNodeId = NodeId.Null
                };
            }

            var (foundProperty, dictionaryKey, collectionIndex) = FindPropertyForBrowseName(
                parentRegisteredSubject, item.BrowseName);

            if (foundProperty is null)
            {
                return new AddNodesResult
                {
                    StatusCode = StatusCodes.BadBrowseNameInvalid,
                    AddedNodeId = NodeId.Null
                };
            }

            property = foundProperty;
            index = dictionaryKey ?? (object?)collectionIndex;

            try
            {
                var referenceDescription = new ReferenceDescription
                {
                    BrowseName = item.BrowseName,
                    DisplayName = new LocalizedText(item.BrowseName.Name),
                    NodeClass = NodeClass.Object
                };

                if (property.IsSubjectCollection || property.IsSubjectDictionary)
                {
                    newSubject = _configuration.SubjectFactory
                        .CreateCollectionSubjectAsync(property, referenceDescription,
                            index, session: null!, CancellationToken.None)
                        .GetAwaiter().GetResult();
                }
                else
                {
                    newSubject = _configuration.SubjectFactory
                        .CreateSubjectAsync(property, referenceDescription,
                            session: null!, CancellationToken.None)
                        .GetAwaiter().GetResult();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create subject for remote AddNodes request.");
                return new AddNodesResult
                {
                    StatusCode = StatusCodes.BadInternalError,
                    AddedNodeId = NodeId.Null
                };
            }

            AddSubjectToProperty(property, newSubject, dictionaryKey, collectionIndex);
        }
        finally
        {
            _structureLock.Release();
        }

        // CreateDynamicSubjectNodes acquires _structureLock internally,
        // so it must run after the lock above is released.
        var lifecycleChange = new SubjectLifecycleChange
        {
            Subject = newSubject,
            Property = property.Reference,
            Index = index,
            ReferenceCount = 0
        };

        var createdNode = CreateDynamicSubjectNodes(lifecycleChange);
        if (createdNode is null)
        {
            return new AddNodesResult
            {
                StatusCode = StatusCodes.BadInternalError,
                AddedNodeId = NodeId.Null
            };
        }

        _logger.LogInformation(
            "Remote AddNodes: created subject for browse name '{BrowseName}' with NodeId {NodeId}.",
            item.BrowseName, createdNode.NodeId);

        FireModelChangeEvent(ModelChangeStructureVerbMask.NodeAdded, createdNode.NodeId);

        return new AddNodesResult
        {
            StatusCode = StatusCodes.Good,
            AddedNodeId = createdNode.NodeId
        };
    }

    /// <summary>
    /// Processes a DeleteNodes request from a remote client.
    /// Removes the subject from the local model and cleans up the OPC UA nodes.
    /// Called by OpcUaStandardServer.DeleteNodesAsync when AllowRemoteNodeManagement is enabled.
    /// </summary>
    /// <returns>The status code for the delete operation.</returns>
    public StatusCode HandleRemoteDeleteNode(DeleteNodesItem item)
    {
        IInterceptorSubject subject;

        _structureLock.Wait();
        try
        {
            var (registeredSubject, foundSubject) = FindSubjectByNodeId(item.NodeId);
            if (registeredSubject is null || foundSubject is null)
            {
                return StatusCodes.BadNodeIdUnknown;
            }

            subject = foundSubject;

            var parents = registeredSubject.Parents;
            if (parents.Length == 0)
            {
                _logger.LogWarning(
                    "Remote DeleteNodes: subject for NodeId {NodeId} has no parent property.",
                    item.NodeId);
                return StatusCodes.BadInternalError;
            }

            var parent = parents[0];
            RemoveSubjectFromProperty(parent.Property, subject, parent.Index);
        }
        finally
        {
            _structureLock.Release();
        }

        // RemoveSubjectNodes acquires _structureLock internally,
        // so it must run after the lock above is released.
        RemoveSubjectNodes(subject);

        _logger.LogInformation(
            "Remote DeleteNodes: removed subject for NodeId {NodeId}.",
            item.NodeId);

        FireModelChangeEvent(ModelChangeStructureVerbMask.NodeDeleted, item.NodeId);

        return StatusCodes.Good;
    }

    /// <summary>
    /// Finds a RegisteredSubject and its IInterceptorSubject given a NodeId.
    /// Checks both the _subjects dictionary and the root subject.
    /// </summary>
    private (RegisteredSubject? RegisteredSubject, IInterceptorSubject? Subject) FindSubjectByNodeId(NodeId nodeId)
    {
        // Check root subject
        var rootRegistered = _subject.TryGetRegisteredSubject();
        if (rootRegistered is not null)
        {
            NodeId? rootNodeId = null;
            if (_configuration.RootName is not null)
            {
                rootNodeId = new NodeId(_configuration.RootName, NamespaceIndex);
            }
            else if (_subjects.TryGetValue(rootRegistered, out var rootNode))
            {
                rootNodeId = rootNode.NodeId;
            }

            if (rootNodeId is not null && rootNodeId == nodeId)
            {
                return (rootRegistered, _subject);
            }
        }

        // Check child subjects
        foreach (var (registeredSubject, nodeState) in _subjects)
        {
            if (nodeState.NodeId == nodeId)
            {
                return (registeredSubject, registeredSubject.Subject);
            }
        }

        // Check if it's a container folder (collection/dictionary folder nodes).
        // Container folders don't map directly to subjects, but they parent
        // the subjects inside. For AddNodes, the parentId may be a container folder.
        // In that case, we need to find the subject that owns the container.
        return (null, null);
    }

    /// <summary>
    /// Finds a RegisteredSubject whose collection/dictionary container folder matches the given NodeId.
    /// Returns the parent subject and the structural property that owns the container.
    /// </summary>
    public (RegisteredSubject? ParentSubject, RegisteredSubjectProperty? Property) FindContainerOwner(NodeId containerNodeId)
    {
        // The container folder's NodeId is typically: parentPath + propertyName
        // We need to find which subject+property created this folder.
        var containerIdStr = containerNodeId.Identifier as string;
        if (containerIdStr is null)
        {
            return (null, null);
        }

        // Check root subject
        var rootRegistered = _subject.TryGetRegisteredSubject();
        if (rootRegistered is not null)
        {
            var rootPrefix = _configuration.RootName is not null
                ? _configuration.RootName + PathDelimiter
                : string.Empty;

            foreach (var property in rootRegistered.Properties)
            {
                if (!property.IsSubjectCollection && !property.IsSubjectDictionary)
                {
                    continue;
                }

                var propertyName = property.ResolvePropertyName(_nodeMapper);
                if (propertyName is not null && containerIdStr == rootPrefix + propertyName)
                {
                    return (rootRegistered, property);
                }
            }
        }

        // Check child subjects
        foreach (var (registeredSubject, nodeState) in _subjects)
        {
            var parentPath = nodeState.NodeId.Identifier is string stringId
                ? stringId + PathDelimiter
                : string.Empty;

            foreach (var property in registeredSubject.Properties)
            {
                if (!property.IsSubjectCollection && !property.IsSubjectDictionary)
                {
                    continue;
                }

                var propertyName = property.ResolvePropertyName(_nodeMapper);
                if (propertyName is not null && containerIdStr == parentPath + propertyName)
                {
                    return (registeredSubject, property);
                }
            }
        }

        return (null, null);
    }

    /// <summary>
    /// Finds the structural property that matches a browse name on a parent subject.
    /// For collections: browse name is "PropertyName[index]"
    /// For dictionaries: browse name is the dictionary key (parent is the container folder)
    /// For references: browse name is the property name
    /// </summary>
    private (RegisteredSubjectProperty? Property, string? DictionaryKey, int? CollectionIndex)
        FindPropertyForBrowseName(RegisteredSubject parentSubject, QualifiedName browseName)
    {
        var browseNameStr = browseName.Name;

        foreach (var property in parentSubject.Properties)
        {
            var propertyName = property.ResolvePropertyName(_nodeMapper);
            if (propertyName is null)
            {
                continue;
            }

            if (property.IsSubjectReference && propertyName == browseNameStr)
            {
                return (property, null, null);
            }

            if (property.IsSubjectCollection)
            {
                // Collection browse names: "PropertyName[index]"
                if (browseNameStr.StartsWith(propertyName + "[") && browseNameStr.EndsWith("]"))
                {
                    var indexStr = browseNameStr.Substring(
                        propertyName.Length + 1,
                        browseNameStr.Length - propertyName.Length - 2);

                    if (int.TryParse(indexStr, out var index))
                    {
                        return (property, null, index);
                    }
                }
            }

            if (property.IsSubjectDictionary)
            {
                // For dictionaries, the parentId is the container folder.
                // The browse name is the dictionary key itself.
                return (property, browseNameStr, null);
            }
        }

        return (null, null, null);
    }

    /// <summary>
    /// Adds a subject to a structural property (collection, dictionary, or reference).
    /// Uses SetValueFromSource to tag the change as originating from the server.
    /// </summary>
    private void AddSubjectToProperty(
        RegisteredSubjectProperty property,
        IInterceptorSubject newSubject,
        string? dictionaryKey,
        int? collectionIndex)
    {
        var propertyRef = property.Reference;

        if (property.IsSubjectReference)
        {
            propertyRef.SetValueFromSource(_serverService, null, null, newSubject);
        }
        else if (property.IsSubjectCollection)
        {
            var currentValue = property.GetValue();
            var newCollection = AppendToCollection(currentValue, newSubject, property.Type);
            propertyRef.SetValueFromSource(_serverService, null, null, newCollection);
        }
        else if (property.IsSubjectDictionary && dictionaryKey is not null)
        {
            var currentValue = property.GetValue();
            var newDictionary = AddToDictionary(currentValue, dictionaryKey, newSubject, property.Type);
            propertyRef.SetValueFromSource(_serverService, null, null, newDictionary);
        }
    }

    /// <summary>
    /// Removes a subject from a structural property (collection, dictionary, or reference).
    /// Uses SetValueFromSource to tag the change as originating from the server.
    /// </summary>
    private void RemoveSubjectFromProperty(
        RegisteredSubjectProperty property,
        IInterceptorSubject subject,
        object? index)
    {
        var propertyRef = property.Reference;

        if (property.IsSubjectReference)
        {
            var factory = _configuration.SubjectFactory;
            if (factory is not null)
            {
                var emptySubject = factory.CreateSubjectAsync(property, new ReferenceDescription
                {
                    BrowseName = new QualifiedName(property.Name, NamespaceIndex),
                    DisplayName = new LocalizedText(property.Name),
                    NodeClass = NodeClass.Object
                }, session: null!, CancellationToken.None).GetAwaiter().GetResult();

                propertyRef.SetValueFromSource(_serverService, null, null, emptySubject);
            }
        }
        else if (property.IsSubjectCollection)
        {
            var currentValue = property.GetValue();
            var newCollection = RemoveFromCollection(currentValue, subject, property.Type);
            propertyRef.SetValueFromSource(_serverService, null, null, newCollection);
        }
        else if (property.IsSubjectDictionary)
        {
            var currentValue = property.GetValue();
            var newDictionary = RemoveFromDictionary(currentValue, subject, property.Type);
            propertyRef.SetValueFromSource(_serverService, null, null, newDictionary);
        }
    }

    private static object AppendToCollection(object? currentValue, IInterceptorSubject newItem, Type propertyType)
    {
        if (propertyType.IsArray)
        {
            var elementType = propertyType.GetElementType()!;
            var existingArray = currentValue as Array ?? Array.CreateInstance(elementType, 0);
            var newArray = Array.CreateInstance(elementType, existingArray.Length + 1);
            Array.Copy(existingArray, newArray, existingArray.Length);
            newArray.SetValue(newItem, existingArray.Length);
            return newArray;
        }

        if (currentValue is IList list)
        {
            var newList = (IList)Activator.CreateInstance(currentValue.GetType())!;
            foreach (var item in list)
            {
                newList.Add(item);
            }
            newList.Add(newItem);
            return newList;
        }

        var subjectType = newItem.GetType();
        var fallbackArray = Array.CreateInstance(subjectType, 1);
        fallbackArray.SetValue(newItem, 0);
        return fallbackArray;
    }

    private static object RemoveFromCollection(object? currentValue, IInterceptorSubject itemToRemove, Type propertyType)
    {
        if (propertyType.IsArray)
        {
            var elementType = propertyType.GetElementType()!;
            if (currentValue is not Array existingArray)
            {
                return Array.CreateInstance(elementType, 0);
            }

            var items = new List<object>();
            foreach (var item in existingArray)
            {
                if (!ReferenceEquals(item, itemToRemove))
                {
                    items.Add(item);
                }
            }

            var newArray = Array.CreateInstance(elementType, items.Count);
            for (var i = 0; i < items.Count; i++)
            {
                newArray.SetValue(items[i], i);
            }
            return newArray;
        }

        if (currentValue is IList list)
        {
            var newList = (IList)Activator.CreateInstance(currentValue.GetType())!;
            foreach (var item in list)
            {
                if (!ReferenceEquals(item, itemToRemove))
                {
                    newList.Add(item);
                }
            }
            return newList;
        }

        return currentValue ?? Array.Empty<object>();
    }

    private static object AddToDictionary(object? currentValue, string key, IInterceptorSubject newItem, Type propertyType)
    {
        if (currentValue is IDictionary dict)
        {
            var newDict = (IDictionary)Activator.CreateInstance(currentValue.GetType())!;
            foreach (DictionaryEntry entry in dict)
            {
                newDict[entry.Key] = entry.Value;
            }
            newDict[key] = newItem;
            return newDict;
        }

        var newDictionary = (IDictionary)Activator.CreateInstance(propertyType)!;
        newDictionary[key] = newItem;
        return newDictionary;
    }

    private static object RemoveFromDictionary(object? currentValue, IInterceptorSubject itemToRemove, Type propertyType)
    {
        if (currentValue is IDictionary dict)
        {
            var newDict = (IDictionary)Activator.CreateInstance(currentValue.GetType())!;
            foreach (DictionaryEntry entry in dict)
            {
                if (!ReferenceEquals(entry.Value, itemToRemove))
                {
                    newDict[entry.Key] = entry.Value;
                }
            }
            return newDict;
        }

        return currentValue ?? Activator.CreateInstance(propertyType)!;
    }
}