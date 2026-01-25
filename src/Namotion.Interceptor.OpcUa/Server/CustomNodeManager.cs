using System.Collections.Concurrent;
using System.Reflection;
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
    private readonly OpcUaSubjectServerBackgroundService _source;
    private readonly OpcUaServerConfiguration _configuration;
    private readonly IOpcUaNodeMapper _nodeMapper;

    private readonly ConcurrentDictionary<RegisteredSubject, NodeState> _subjects = new();

    public CustomNodeManager(
        IInterceptorSubject subject,
        OpcUaSubjectServerBackgroundService source,
        IServerInternal server,
        ApplicationConfiguration applicationConfiguration,
        OpcUaServerConfiguration configuration) :
        base(server, applicationConfiguration, configuration.GetNamespaceUris())
    {
        _subject = subject;
        _source = source;
        _configuration = configuration;
        _nodeMapper = configuration.NodeMapper;
    }

    protected override NodeStateCollection LoadPredefinedNodes(ISystemContext context)
    {
        var collection = base.LoadPredefinedNodes(context);
        _configuration.LoadPredefinedNodes(collection, context);
        return collection;
    }

    public void ClearPropertyData()
    {
        foreach (var node in PredefinedNodes.Values)
        {
            if (node is BaseDataVariableState { Handle: PropertyReference property })
            {
                property.RemovePropertyData(OpcUaSubjectServerBackgroundService.OpcVariableKey);
            }
        }
    }

    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        base.CreateAddressSpace(externalReferences);

        var registeredSubject = _subject.TryGetRegisteredSubject();
        if (registeredSubject is not null)
        {
            if (_configuration.RootName is not null)
            {
                var node = CreateFolderNode(ObjectIds.ObjectsFolder, 
                    new NodeId(_configuration.RootName, NamespaceIndex), _configuration.RootName, null, null);

                CreateObjectNode(node.NodeId, registeredSubject, _configuration.RootName + PathDelimiter);
            }
            else
            {
                CreateObjectNode(ObjectIds.ObjectsFolder, registeredSubject, string.Empty);
            }
        }
    }

    /// <summary>
    /// Removes nodes for a detached subject. Idempotent - safe to call multiple times.
    /// Uses DeleteNode to properly cleanup nodes and event handlers, preventing memory leaks.
    /// </summary>
    public void RemoveSubjectNodes(IInterceptorSubject subject)
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

        // Remove object nodes
        foreach (var kvp in _subjects)
        {
            if (kvp.Key.Subject == subject)
            {
                DeleteNode(SystemContext, kvp.Value.NodeId);
                _subjects.TryRemove(kvp.Key, out _);
            }
        }
    }

    private void CreateObjectNode(NodeId parentNodeId, RegisteredSubject subject, string prefix)
    {
        foreach (var property in subject.Properties)
        {
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
                        if (nodeConfiguration?.NodeClass == Mapping.OpcUaNodeClass.Variable)
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
        var browseName = GetBrowseName(propertyName, property, child.Index);
        var referenceTypeId = GetReferenceTypeId(property);

        CreateChildObject(property, browseName, child.Subject, path, parentNodeId, referenceTypeId);
    }

    private void CreateArrayObjectNode(string propertyName, RegisteredSubjectProperty property, ICollection<SubjectPropertyChild> children, NodeId parentNodeId, string parentPath)
    {
        var nodeId = GetNodeId(property, parentPath + propertyName);
        var browseName = GetBrowseName(propertyName, property, null);

        var typeDefinitionId = GetTypeDefinitionId(property);
        var referenceTypeId = GetReferenceTypeId(property);

        var propertyNode = CreateFolderNode(parentNodeId, nodeId, browseName, typeDefinitionId, referenceTypeId);

        // Child objects below the array folder use path: parentPath + propertyName + "[index]"
        var childReferenceTypeId = GetChildReferenceTypeId(property);
        foreach (var child in children)
        {
            var childBrowseName = new QualifiedName($"{propertyName}[{child.Index}]", NamespaceIndex);
            var childPath = $"{parentPath}{propertyName}[{child.Index}]";

            CreateChildObject(property, childBrowseName, child.Subject, childPath, propertyNode.NodeId, childReferenceTypeId);
        }
    }

    private void CreateDictionaryObjectNode(string propertyName, RegisteredSubjectProperty property, ICollection<SubjectPropertyChild> children, NodeId parentNodeId, string parentPath)
    {
        var nodeId = GetNodeId(property, parentPath + propertyName);
        var browseName = GetBrowseName(propertyName, property, null);

        var typeDefinitionId = GetTypeDefinitionId(property);
        var referenceTypeId = GetReferenceTypeId(property);

        var propertyNode = CreateFolderNode(parentNodeId, nodeId, browseName, typeDefinitionId, referenceTypeId);
        var childReferenceTypeId = GetChildReferenceTypeId(property);
        foreach (var child in children)
        {
            var childBrowseName = new QualifiedName(child.Index?.ToString(), NamespaceIndex);
            var childPath = parentPath + propertyName + PathDelimiter + child.Index;

            CreateChildObject(property, childBrowseName, child.Subject, childPath, propertyNode.NodeId, childReferenceTypeId);
        }
    }

    private void CreateVariableNode(string propertyName, RegisteredSubjectProperty property, NodeId parentNodeId, string parentPath)
    {
        var value = _configuration.ValueConverter.ConvertToNodeValue(property.GetValue(), property);
        var typeInfo = _configuration.ValueConverter.GetNodeTypeInfo(property.Type);

        var nodeId = GetNodeId(property, parentPath + propertyName);
        var browseName = GetBrowseName(propertyName, property, null);

        var referenceTypeId = GetReferenceTypeId(property);
        var dataTypeOverride = GetDataTypeOverride(property);
        var nodeConfiguration = _nodeMapper.TryGetNodeConfiguration(property);
        var variableNode = CreateVariableNode(parentNodeId, nodeId, browseName, typeInfo, referenceTypeId, dataTypeOverride, nodeConfiguration);
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
        variableNode.StateChanged += (_, _, changes) =>
        {
            if (changes.HasFlag(NodeStateChangeMasks.Value))
            {
                // Lock on node to prevent race conditions with WriteToSourceAsync

                DateTimeOffset timestamp;
                object? nodeValue;
                lock (variableNode)
                {
                    timestamp = variableNode.Timestamp;
                    nodeValue = variableNode.Value;
                }

                _source.UpdateProperty(property.Reference, timestamp, nodeValue);
            }
        };

        property.Reference.SetPropertyData(OpcUaSubjectServerBackgroundService.OpcVariableKey, variableNode);
    }

    private void CreateVariableNodeForSubject(string propertyName, RegisteredSubjectProperty property, NodeId parentNodeId, string parentPath)
    {
        var path = parentPath + propertyName;
        var nodeId = GetNodeId(property, path);
        var browseName = GetBrowseName(propertyName, property, null);
        var referenceTypeId = GetReferenceTypeId(property);

        // Get the child subject for accessing [OpcUaValue] property
        var childSubject = property.Children.SingleOrDefault().Subject?.TryGetRegisteredSubject();

        // Find the [OpcUaValue] property to get the value and type
        object? value = null;
        var typeInfo = _configuration.ValueConverter.GetNodeTypeInfo(typeof(object));
        RegisteredSubjectProperty? valueProperty = null;

        if (childSubject != null)
        {
            foreach (var childProperty in childSubject.Properties)
            {
                var childConfig = _nodeMapper.TryGetNodeConfiguration(childProperty);
                if (childConfig?.IsValue == true)
                {
                    value = _configuration.ValueConverter.ConvertToNodeValue(childProperty.GetValue(), childProperty);
                    typeInfo = _configuration.ValueConverter.GetNodeTypeInfo(childProperty.Type);
                    valueProperty = childProperty;
                    break;
                }
            }
        }

        var variableNode = CreateVariableNode(parentNodeId, nodeId, browseName, typeInfo, referenceTypeId);
        variableNode.Handle = property.Reference;
        variableNode.Value = value;

        // Set access level based on value property (if found) or read-only
        if (valueProperty?.HasSetter == true)
        {
            variableNode.AccessLevel = AccessLevels.CurrentReadOrWrite;
            variableNode.UserAccessLevel = AccessLevels.CurrentReadOrWrite;
        }
        else
        {
            variableNode.AccessLevel = AccessLevels.CurrentRead;
            variableNode.UserAccessLevel = AccessLevels.CurrentRead;
        }

        // Create child properties of the VariableNode (excluding the value property)
        if (childSubject != null)
        {
            foreach (var childProperty in childSubject.Properties)
            {
                var childConfig = _nodeMapper.TryGetNodeConfiguration(childProperty);
                if (childConfig?.IsValue != true) // Skip the value property itself
                {
                    var childName = childProperty.ResolvePropertyName(_nodeMapper);
                    if (childName != null)
                    {
                        CreateVariableNode(childName, childProperty, variableNode.NodeId, path + PathDelimiter);
                    }
                }
            }
        }

        property.Reference.SetPropertyData(OpcUaSubjectServerBackgroundService.OpcVariableKey, variableNode);
    }

    private NodeId GetNodeId(RegisteredSubjectProperty property, string fullPath)
    {
        var nodeConfiguration = _nodeMapper.TryGetNodeConfiguration(property);
        if (nodeConfiguration is { NodeIdentifier: not null })
        {
            return nodeConfiguration.NodeNamespaceUri is not null ?
                NodeId.Create(nodeConfiguration.NodeIdentifier, nodeConfiguration.NodeNamespaceUri, SystemContext.NamespaceUris) :
                new NodeId(nodeConfiguration.NodeIdentifier, NamespaceIndex);
        }

        return new NodeId(fullPath, NamespaceIndex);
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
            // Create new node and add to dictionary (thread-safe)
            _subjects.GetOrAdd(registeredSubject, _ =>
            {
                var nodeId = GetNodeId(property, path);
                var typeDefinitionId = GetTypeDefinitionId(subject);

                var node = CreateObjectNode(parentNodeId, nodeId, browseName, typeDefinitionId, referenceTypeId);
                CreateObjectNode(node.NodeId, registeredSubject, path + PathDelimiter);

                return node;
            });
        }
    }

    private NodeId? GetReferenceTypeId(RegisteredSubjectProperty property)
    {
        var nodeConfiguration = _nodeMapper.TryGetNodeConfiguration(property);
        if (nodeConfiguration?.ReferenceType is not null)
        {
            return typeof(ReferenceTypeIds).GetField(nodeConfiguration.ReferenceType)?.GetValue(null) as NodeId;
        }

        return null;
    }

    private NodeId? GetChildReferenceTypeId(RegisteredSubjectProperty property)
    {
        var nodeConfiguration = _nodeMapper.TryGetNodeConfiguration(property);
        if (nodeConfiguration?.ItemReferenceType is not null)
        {
            return typeof(ReferenceTypeIds).GetField(nodeConfiguration.ItemReferenceType)?.GetValue(null) as NodeId;
        }

        return null;
    }

    private NodeId? GetTypeDefinitionId(RegisteredSubjectProperty property)
    {
        var nodeConfiguration = _nodeMapper.TryGetNodeConfiguration(property);
        return GetTypeDefinitionId(nodeConfiguration?.TypeDefinition, nodeConfiguration?.TypeDefinitionNamespace);
    }

    private NodeId? GetTypeDefinitionId(IInterceptorSubject subject)
    {
        // For subjects, check if type has OpcUaNode attribute at class level
        var typeAttribute = subject.GetType().GetCustomAttribute<OpcUaNodeAttribute>();
        if (typeAttribute is not null)
        {
            return GetTypeDefinitionId(typeAttribute.TypeDefinition, typeAttribute.TypeDefinitionNamespace);
        }

        return null;
    }

    private NodeId? GetTypeDefinitionId(string? typeDefinition, string? typeDefinitionNamespace)
    {
        if (typeDefinition is null)
        {
            return null;
        }

        if (typeDefinitionNamespace is not null)
        {
            var nodeId = NodeId.Create(
                typeDefinition,
                typeDefinitionNamespace,
                SystemContext.NamespaceUris);

            return PredefinedNodes.Values.SingleOrDefault(n =>
                    n.BrowseName.Name == nodeId.Identifier.ToString() &&
                    n.BrowseName.NamespaceIndex == nodeId.NamespaceIndex)?
                .NodeId;
        }

        return typeof(ObjectTypeIds).GetField(typeDefinition)?.GetValue(null) as NodeId;
    }

    private QualifiedName GetBrowseName(string propertyName, RegisteredSubjectProperty property, object? index)
    {
        var nodeConfiguration = _nodeMapper.TryGetNodeConfiguration(property);
        if (nodeConfiguration?.BrowseName is null)
        {
            return new QualifiedName(propertyName + (index is not null ? $"[{index}]" : string.Empty), NamespaceIndex);
        }

        if (nodeConfiguration.BrowseNamespaceUri is not null)
        {
            return new QualifiedName(nodeConfiguration.BrowseName, (ushort)SystemContext.NamespaceUris.GetIndex(nodeConfiguration.BrowseNamespaceUri));
        }

        return new QualifiedName(nodeConfiguration.BrowseName, NamespaceIndex);
    }

    private FolderState CreateFolderNode(
        NodeId parentNodeId,
        NodeId nodeId,
        QualifiedName browseName,
        NodeId? typeDefinition,
        NodeId? referenceType,
        OpcUaNodeConfiguration? nodeConfiguration = null)
    {
        var parentNode = FindNodeInAddressSpace(parentNodeId);

        var folderNode = new FolderState(parentNode)
        {
            NodeId = nodeId,
            BrowseName = browseName,
            DisplayName = new LocalizedText(nodeConfiguration?.DisplayName ?? browseName.Name),
            Description = nodeConfiguration?.Description is not null
                ? new LocalizedText(nodeConfiguration.Description)
                : null,
            TypeDefinitionId = typeDefinition ?? ObjectTypeIds.FolderType,
            WriteMask = AttributeWriteMask.None,
            UserWriteMask = AttributeWriteMask.None,
            ReferenceTypeId = referenceType ?? ReferenceTypeIds.HasComponent
        };

        parentNode?.AddChild(folderNode);

        AddPredefinedNode(SystemContext, folderNode);
        return folderNode;
    }
    
    private BaseObjectState CreateObjectNode(
        NodeId parentNodeId,
        NodeId nodeId,
        QualifiedName browseName,
        NodeId? typeDefinition,
        NodeId? referenceType,
        OpcUaNodeConfiguration? nodeConfiguration = null)
    {
        var parentNode = FindNodeInAddressSpace(parentNodeId);

        var objectNode = new BaseObjectState(parentNode)
        {
            NodeId = nodeId,
            BrowseName = browseName,
            DisplayName = new LocalizedText(nodeConfiguration?.DisplayName ?? browseName.Name),
            Description = nodeConfiguration?.Description is not null
                ? new LocalizedText(nodeConfiguration.Description)
                : null,
            TypeDefinitionId = typeDefinition ?? ObjectTypeIds.BaseObjectType,
            WriteMask = AttributeWriteMask.None,
            UserWriteMask = AttributeWriteMask.None,
            ReferenceTypeId = referenceType ?? ReferenceTypeIds.HasComponent
        };

        parentNode?.AddChild(objectNode);

        AddPredefinedNode(SystemContext, objectNode);
        return objectNode;
    }

    private BaseDataVariableState CreateVariableNode(
        NodeId parentNodeId, NodeId nodeId, QualifiedName browseName,
        Opc.Ua.TypeInfo dataType, NodeId? referenceType, NodeId? dataTypeOverride = null,
        OpcUaNodeConfiguration? nodeConfiguration = null)
    {
        var parentNode = FindNodeInAddressSpace(parentNodeId);

        var variable = new BaseDataVariableState(parentNode)
        {
            NodeId = nodeId,

            SymbolicName = browseName.Name,
            BrowseName = browseName,
            DisplayName = new LocalizedText(nodeConfiguration?.DisplayName ?? browseName.Name),
            Description = nodeConfiguration?.Description is not null
                ? new LocalizedText(nodeConfiguration.Description)
                : null,

            TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
            DataType = dataTypeOverride ?? Opc.Ua.TypeInfo.GetDataTypeId(dataType),
            ValueRank = dataType.ValueRank,
            AccessLevel = AccessLevels.CurrentReadOrWrite,
            UserAccessLevel = AccessLevels.CurrentReadOrWrite,
            StatusCode = StatusCodes.Good,
            Timestamp = DateTime.UtcNow, // TODO: Is using now correct here?

            ReferenceTypeId = referenceType ?? ReferenceTypeIds.HasProperty
        };

        parentNode?.AddChild(variable);

        AddPredefinedNode(SystemContext, variable);
        return variable;
    }

    private NodeId? GetDataTypeOverride(RegisteredSubjectProperty property)
    {
        var nodeConfiguration = _nodeMapper.TryGetNodeConfiguration(property);
        if (nodeConfiguration?.DataType is { } dataTypeName)
        {
            // Try to resolve from DataTypeIds
            var field = typeof(DataTypeIds).GetField(dataTypeName);
            return field?.GetValue(null) as NodeId;
        }
        return null;
    }
}