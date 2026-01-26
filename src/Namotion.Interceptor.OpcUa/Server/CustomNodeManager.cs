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
        var rootSubject = _subject.TryGetRegisteredSubject();
        if (rootSubject != null)
        {
            foreach (var property in rootSubject.Properties)
            {
                property.Reference.RemovePropertyData(OpcUaSubjectServerBackgroundService.OpcVariableKey);
                ClearAttributePropertyData(property);
            }
        }

        foreach (var subject in _subjects.Keys)
        {
            foreach (var property in subject.Properties)
            {
                property.Reference.RemovePropertyData(OpcUaSubjectServerBackgroundService.OpcVariableKey);
                ClearAttributePropertyData(property);
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
        var browseName = GetBrowseName(propertyName, property, child.Index);
        var referenceTypeId = GetReferenceTypeId(property);

        CreateChildObject(property, browseName, child.Subject, path, parentNodeId, referenceTypeId);
    }

    private void CreateArrayObjectNode(string propertyName, RegisteredSubjectProperty property, ICollection<SubjectPropertyChild> children, NodeId parentNodeId, string parentPath)
    {
        // Cache node configuration to avoid repeated lookups
        var nodeConfiguration = _nodeMapper.TryGetNodeConfiguration(property);

        var nodeId = GetNodeId(nodeConfiguration, parentPath + propertyName);
        var browseName = GetBrowseName(propertyName, nodeConfiguration, null);

        var typeDefinitionId = GetTypeDefinitionId(nodeConfiguration);
        var referenceTypeId = GetReferenceTypeId(nodeConfiguration);

        var propertyNode = CreateFolderNode(parentNodeId, nodeId, browseName, typeDefinitionId, referenceTypeId);

        // Child objects below the array folder use path: parentPath + propertyName + "[index]"
        var childReferenceTypeId = GetChildReferenceTypeId(nodeConfiguration);
        foreach (var child in children)
        {
            var childBrowseName = new QualifiedName($"{propertyName}[{child.Index}]", NamespaceIndex);
            var childPath = $"{parentPath}{propertyName}[{child.Index}]";

            CreateChildObject(property, childBrowseName, child.Subject, childPath, propertyNode.NodeId, childReferenceTypeId);
        }
    }

    private void CreateDictionaryObjectNode(string propertyName, RegisteredSubjectProperty property, ICollection<SubjectPropertyChild> children, NodeId parentNodeId, string parentPath)
    {
        // Cache node configuration to avoid repeated lookups
        var nodeConfiguration = _nodeMapper.TryGetNodeConfiguration(property);

        var nodeId = GetNodeId(nodeConfiguration, parentPath + propertyName);
        var browseName = GetBrowseName(propertyName, nodeConfiguration, null);

        var typeDefinitionId = GetTypeDefinitionId(nodeConfiguration);
        var referenceTypeId = GetReferenceTypeId(nodeConfiguration);

        var propertyNode = CreateFolderNode(parentNodeId, nodeId, browseName, typeDefinitionId, referenceTypeId);
        var childReferenceTypeId = GetChildReferenceTypeId(nodeConfiguration);
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

        var nodeId = GetNodeId(actualConfigurationProperty, parentPath + propertyName);
        var browseName = GetBrowseName(propertyName, actualConfigurationProperty, null);
        var referenceTypeId = GetReferenceTypeId(actualConfigurationProperty);
        var dataTypeOverride = GetDataTypeOverride(property);
        var nodeConfiguration = _nodeMapper.TryGetNodeConfiguration(actualConfigurationProperty);

        var variableNode = ConfigureVariableNode(property, parentNodeId, nodeId, browseName, referenceTypeId, dataTypeOverride, nodeConfiguration);

        property.Reference.SetPropertyData(OpcUaSubjectServerBackgroundService.OpcVariableKey, variableNode);

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
            var referenceTypeId = GetReferenceTypeId(attribute) ?? ReferenceTypeIds.HasProperty;

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
        var nodeId = GetNodeId(attribute, path);
        var browseName = GetBrowseName(attributeName, attribute, null);
        var dataTypeOverride = GetDataTypeOverride(attribute);
        var nodeConfiguration = _nodeMapper.TryGetNodeConfiguration(attribute);

        var variableNode = ConfigureVariableNode(attribute, parentNodeId, nodeId, browseName, referenceTypeId, dataTypeOverride, nodeConfiguration);

        attribute.Reference.SetPropertyData(OpcUaSubjectServerBackgroundService.OpcVariableKey, variableNode);

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

    private NodeId GetNodeId(RegisteredSubjectProperty property, string fullPath) =>
        GetNodeId(_nodeMapper.TryGetNodeConfiguration(property), fullPath);

    private NodeId GetNodeId(OpcUaNodeConfiguration? nodeConfiguration, string fullPath)
    {
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

    private NodeId? GetReferenceTypeId(RegisteredSubjectProperty property) =>
        GetReferenceTypeId(_nodeMapper.TryGetNodeConfiguration(property));

    private NodeId? GetReferenceTypeId(OpcUaNodeConfiguration? nodeConfiguration)
    {
        if (nodeConfiguration?.ReferenceType is { } name)
        {
            if (ReferenceTypeIdLookup.Value.TryGetValue(name, out var nodeId))
            {
                return nodeId;
            }

            _logger.LogWarning(
                "Unknown ReferenceType '{ReferenceType}'. Using default.",
                name);
        }

        return null;
    }

    private NodeId? GetChildReferenceTypeId(OpcUaNodeConfiguration? nodeConfiguration)
    {
        if (nodeConfiguration?.ItemReferenceType is { } name)
        {
            if (ReferenceTypeIdLookup.Value.TryGetValue(name, out var nodeId))
            {
                return nodeId;
            }

            _logger.LogWarning(
                "Unknown ItemReferenceType '{ItemReferenceType}'. Using default.",
                name);
        }

        return null;
    }

    private NodeId? GetTypeDefinitionId(OpcUaNodeConfiguration? nodeConfiguration) =>
        GetTypeDefinitionId(nodeConfiguration?.TypeDefinition, nodeConfiguration?.TypeDefinitionNamespace);

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

            // TODO(perf): Consider caching TypeDefinitionId lookups - this O(n) search
            // on PredefinedNodes.Values could become expensive with large nodeset imports.
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

    private QualifiedName GetBrowseName(string propertyName, RegisteredSubjectProperty property, object? index) =>
        GetBrowseName(propertyName, _nodeMapper.TryGetNodeConfiguration(property), index);

    private QualifiedName GetBrowseName(string propertyName, OpcUaNodeConfiguration? nodeConfiguration, object? index)
    {
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

        if (nodeConfiguration?.EventNotifier is { } eventNotifier && eventNotifier != byte.MaxValue)
        {
            folderNode.EventNotifier = eventNotifier;
        }

        parentNode?.AddChild(folderNode);

        AddPredefinedNode(SystemContext, folderNode);
        AddModellingRuleReference(folderNode, nodeConfiguration);
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

        if (nodeConfiguration?.EventNotifier is { } eventNotifier && eventNotifier != byte.MaxValue)
        {
            objectNode.EventNotifier = eventNotifier;
        }

        parentNode?.AddChild(objectNode);

        AddPredefinedNode(SystemContext, objectNode);
        AddModellingRuleReference(objectNode, nodeConfiguration);
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
        AddModellingRuleReference(variable, nodeConfiguration);
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

    private void AddModellingRuleReference(NodeState node, OpcUaNodeConfiguration? config)
    {
        if (config?.ModellingRule is null or ModellingRule.Unset)
        {
            return;
        }

        var modellingRuleNodeId = GetModellingRuleNodeId(config.ModellingRule.Value);
        if (modellingRuleNodeId is not null)
        {
            node.AddReference(ReferenceTypeIds.HasModellingRule, false, modellingRuleNodeId);
        }
    }

    private static NodeId? GetModellingRuleNodeId(ModellingRule modellingRule)
    {
        return modellingRule switch
        {
            ModellingRule.Mandatory => ObjectIds.ModellingRule_Mandatory,
            ModellingRule.Optional => ObjectIds.ModellingRule_Optional,
            ModellingRule.MandatoryPlaceholder => ObjectIds.ModellingRule_MandatoryPlaceholder,
            ModellingRule.OptionalPlaceholder => ObjectIds.ModellingRule_OptionalPlaceholder,
            ModellingRule.ExposesItsArray => ObjectIds.ModellingRule_ExposesItsArray,
            _ => null
        };
    }
}