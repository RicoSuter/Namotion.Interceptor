using System.Reflection;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger _logger;

    private readonly SemaphoreSlim _structureLock = new(1, 1);
    private readonly Dictionary<RegisteredSubject, NodeState> _subjects = new();

    private static readonly Lazy<Dictionary<string, NodeId>> ReferenceTypeIdLookup =
        new(() => BuildNodeIdLookup(typeof(ReferenceTypeIds)));
    private static readonly Lazy<Dictionary<string, NodeId>> DataTypeIdLookup =
        new(() => BuildNodeIdLookup(typeof(DataTypeIds)));
    private static readonly Lazy<Dictionary<string, NodeId>> ObjectTypeIdLookup =
        new(() => BuildNodeIdLookup(typeof(ObjectTypeIds)));

    private static Dictionary<string, NodeId> BuildNodeIdLookup(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(NodeId))
            .ToDictionary(f => f.Name, f => (NodeId)f.GetValue(null)!);

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
        _source = source;
        _configuration = configuration;
        _nodeMapper = configuration.NodeMapper;
        _logger = logger;
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

        _structureLock.Wait();
        try
        {
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
                    if (property.Reference.TryGetPropertyData(OpcUaSubjectServerBackgroundService.OpcVariableKey, out var node)
                        && node is BaseDataVariableState variableNode)
                    {
                        DeleteNode(SystemContext, variableNode.NodeId);
                        property.Reference.RemovePropertyData(OpcUaSubjectServerBackgroundService.OpcVariableKey);
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

    private BaseDataVariableState CreateVariableNode(
        string propertyName,
        RegisteredSubjectProperty property,
        NodeId parentNodeId,
        string parentPath,
        RegisteredSubjectProperty? configurationProperty = null)
    {
        // Use configurationProperty for node identity, property for value/type
        var actualConfigurationProperty = configurationProperty ?? property;

        var value = _configuration.ValueConverter.ConvertToNodeValue(property.GetValue(), property);
        var typeInfo = _configuration.ValueConverter.GetNodeTypeInfo(property.Type);

        var nodeId = GetNodeId(actualConfigurationProperty, parentPath + propertyName);
        var browseName = GetBrowseName(propertyName, actualConfigurationProperty, null);

        var referenceTypeId = GetReferenceTypeId(actualConfigurationProperty);
        var dataTypeOverride = GetDataTypeOverride(property);
        var nodeConfiguration = _nodeMapper.TryGetNodeConfiguration(actualConfigurationProperty);
        var variableNode = CreateVariableNode(parentNodeId, nodeId, browseName, typeInfo, referenceTypeId, dataTypeOverride, nodeConfiguration);
        AddAdditionalReferences(variableNode, nodeConfiguration);
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
        RegisteredSubjectProperty? valueProperty = null;
        foreach (var childProperty in childSubject.Properties)
        {
            var childConfig = _nodeMapper.TryGetNodeConfiguration(childProperty);
            if (childConfig?.IsValue == true)
            {
                valueProperty = childProperty;
                break;
            }
        }

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
            // Create new node and add to dictionary (protected by _structureLock)
            var nodeId = GetNodeId(property, path);
            var typeDefinitionId = GetTypeDefinitionId(subject);

            var node = CreateObjectNode(parentNodeId, nodeId, browseName, typeDefinitionId, referenceTypeId);
            var nodeConfiguration = _nodeMapper.TryGetNodeConfiguration(property);
            AddAdditionalReferences(node, nodeConfiguration);
            _subjects[registeredSubject] = node;
            CreateObjectNode(node.NodeId, registeredSubject, path + PathDelimiter);
        }
    }

    private NodeId? GetReferenceTypeId(RegisteredSubjectProperty property)
    {
        var nodeConfiguration = _nodeMapper.TryGetNodeConfiguration(property);
        if (nodeConfiguration?.ReferenceType is { } name)
        {
            if (ReferenceTypeIdLookup.Value.TryGetValue(name, out var nodeId))
            {
                return nodeId;
            }

            _logger.LogWarning(
                "Unknown ReferenceType '{ReferenceType}' on property '{Property}'. Using default.",
                name, property.Name);
        }

        return null;
    }

    private NodeId? GetChildReferenceTypeId(RegisteredSubjectProperty property)
    {
        var nodeConfiguration = _nodeMapper.TryGetNodeConfiguration(property);
        if (nodeConfiguration?.ItemReferenceType is { } name)
        {
            if (ReferenceTypeIdLookup.Value.TryGetValue(name, out var nodeId))
            {
                return nodeId;
            }

            _logger.LogWarning(
                "Unknown ItemReferenceType '{ItemReferenceType}' on property '{Property}'. Using default.",
                name, property.Name);
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

        if (ObjectTypeIdLookup.Value.TryGetValue(typeDefinition, out var objectTypeId))
        {
            return objectTypeId;
        }

        _logger.LogWarning(
            "Unknown TypeDefinition '{TypeDefinition}'. Using default.",
            typeDefinition);

        return null;
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
            if (DataTypeIdLookup.Value.TryGetValue(dataTypeName, out var nodeId))
            {
                return nodeId;
            }

            _logger.LogWarning(
                "Unknown DataType '{DataType}' on property '{Property}'. Using default.",
                dataTypeName, property.Name);
        }
        return null;
    }

    private void AddAdditionalReferences(NodeState node, OpcUaNodeConfiguration? config)
    {
        if (config?.AdditionalReferences is null)
        {
            return;
        }

        foreach (var reference in config.AdditionalReferences)
        {
            if (!ReferenceTypeIdLookup.Value.TryGetValue(reference.ReferenceType, out var referenceTypeId))
            {
                _logger.LogWarning(
                    "Unknown additional reference type '{ReferenceType}'. Skipping reference.",
                    reference.ReferenceType);
                continue;
            }

            var targetNodeId = reference.TargetNamespaceUri is not null
                ? NodeId.Create(reference.TargetNodeId, reference.TargetNamespaceUri, SystemContext.NamespaceUris)
                : new NodeId(reference.TargetNodeId, NamespaceIndex);

            node.AddReference(referenceTypeId, reference.IsForward, targetNodeId);
        }
    }
}