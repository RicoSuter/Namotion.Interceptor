using Microsoft.Extensions.Logging;
using Namotion.Interceptor.OpcUa.Attributes;
using Namotion.Interceptor.OpcUa.Mapping;
using Opc.Ua;
using Opc.Ua.Server;

namespace Namotion.Interceptor.OpcUa.Server;

/// <summary>
/// Factory for creating OPC UA SDK nodes with no knowledge of the domain model.
/// Handles OPC UA SDK primitives: node creation, ID resolution, and reference management.
/// Uses <see cref="OpcUaNodeIdResolver"/> internally to resolve string identifiers to NodeIds
/// with caching for performance.
/// </summary>
internal sealed class OpcUaNodeFactory
{
    private readonly ILogger _logger;
    private readonly OpcUaNodeIdResolver _resolver;

    public OpcUaNodeFactory(ILogger logger)
    {
        _logger = logger;
        _resolver = new OpcUaNodeIdResolver(logger);
    }

    public NodeId GetNodeId(CustomNodeManager manager, OpcUaNodeConfiguration? nodeConfiguration, string fullPath)
    {
        var namespaceIndex = manager.NamespaceIndexes[0];

        if (nodeConfiguration is { NodeIdentifier: not null })
        {
            return nodeConfiguration.NodeNamespaceUri is not null ?
                NodeId.Create(nodeConfiguration.NodeIdentifier, nodeConfiguration.NodeNamespaceUri, manager.GetSystemContext().NamespaceUris) :
                new NodeId(nodeConfiguration.NodeIdentifier, namespaceIndex);
        }

        return new NodeId(fullPath, namespaceIndex);
    }

    public QualifiedName GetBrowseName(CustomNodeManager manager, string name, OpcUaNodeConfiguration? nodeConfiguration, object? index)
    {
        var namespaceIndex = manager.NamespaceIndexes[0];

        if (nodeConfiguration?.BrowseName is null)
        {
            return new QualifiedName(name + (index is not null ? $"[{index}]" : string.Empty), namespaceIndex);
        }

        if (nodeConfiguration.BrowseNamespaceUri is not null)
        {
            return new QualifiedName(nodeConfiguration.BrowseName, (ushort)manager.GetSystemContext().NamespaceUris.GetIndex(nodeConfiguration.BrowseNamespaceUri));
        }

        return new QualifiedName(nodeConfiguration.BrowseName, namespaceIndex);
    }

    public NodeId? GetReferenceTypeId(CustomNodeManager manager, OpcUaNodeConfiguration? nodeConfiguration)
    {
        if (nodeConfiguration?.ReferenceType is null)
        {
            return null;
        }

        return _resolver.Resolve(
            nodeConfiguration.ReferenceType,
            nodeConfiguration.ReferenceTypeNamespace,
            NodeIdCategory.ReferenceType,
            manager.GetSystemContext(),
            manager.GetPredefinedNodes());
    }

    public NodeId? GetChildReferenceTypeId(CustomNodeManager manager, OpcUaNodeConfiguration? nodeConfiguration)
    {
        if (nodeConfiguration?.ItemReferenceType is null)
        {
            return null;
        }

        return _resolver.Resolve(
            nodeConfiguration.ItemReferenceType,
            nodeConfiguration.ItemReferenceTypeNamespace,
            NodeIdCategory.ReferenceType,
            manager.GetSystemContext(),
            manager.GetPredefinedNodes());
    }

    public NodeId? GetTypeDefinitionId(CustomNodeManager manager, OpcUaNodeConfiguration? nodeConfiguration) =>
        GetTypeDefinitionIdCore(manager, nodeConfiguration?.TypeDefinition, nodeConfiguration?.TypeDefinitionNamespace);

    public NodeId? GetTypeDefinitionId(CustomNodeManager manager, OpcUaNodeAttribute? typeAttribute) =>
        GetTypeDefinitionIdCore(manager, typeAttribute?.TypeDefinition, typeAttribute?.TypeDefinitionNamespace);

    private NodeId? GetTypeDefinitionIdCore(CustomNodeManager manager, string? typeDefinition, string? typeDefinitionNamespace)
    {
        if (typeDefinition is null)
        {
            return null;
        }

        return _resolver.Resolve(
            typeDefinition,
            typeDefinitionNamespace,
            NodeIdCategory.ObjectType,
            manager.GetSystemContext(),
            manager.GetPredefinedNodes());
    }

    public NodeId? GetDataTypeOverride(CustomNodeManager manager, OpcUaNodeConfiguration? nodeConfiguration)
    {
        if (nodeConfiguration?.DataType is null)
        {
            return null;
        }

        return _resolver.Resolve(
            nodeConfiguration.DataType,
            nodeConfiguration.DataTypeNamespace,
            NodeIdCategory.DataType,
            manager.GetSystemContext(),
            manager.GetPredefinedNodes());
    }

    public FolderState CreateFolderNode(
        CustomNodeManager manager,
        NodeId parentId, NodeId nodeId, QualifiedName browseName,
        NodeId? typeDefinition, NodeId? referenceType, OpcUaNodeConfiguration? nodeConfiguration)
    {
        var parentNode = manager.FindNode(parentId);

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

        manager.AddNode(folderNode);
        AddModellingRuleReference(folderNode, nodeConfiguration);
        return folderNode;
    }

    public BaseObjectState CreateObjectNode(
        CustomNodeManager manager,
        NodeId parentId, NodeId nodeId, QualifiedName browseName,
        NodeId? typeDefinition, NodeId? referenceType, OpcUaNodeConfiguration? nodeConfiguration)
    {
        var parentNode = manager.FindNode(parentId);

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

        manager.AddNode(objectNode);
        AddModellingRuleReference(objectNode, nodeConfiguration);
        return objectNode;
    }

    public BaseDataVariableState CreateVariableNode(
        CustomNodeManager manager,
        NodeId parentId, NodeId nodeId, QualifiedName browseName,
        Opc.Ua.TypeInfo dataType, NodeId? referenceType, NodeId? dataTypeOverride,
        OpcUaNodeConfiguration? nodeConfiguration)
    {
        var parentNode = manager.FindNode(parentId);

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

        manager.AddNode(variable);
        AddModellingRuleReference(variable, nodeConfiguration);
        return variable;
    }

    public void AddAdditionalReferences(CustomNodeManager manager, NodeState node, OpcUaNodeConfiguration? nodeConfiguration)
    {
        if (nodeConfiguration?.AdditionalReferences is null)
        {
            return;
        }

        var namespaceIndex = manager.NamespaceIndexes[0];

        foreach (var reference in nodeConfiguration.AdditionalReferences)
        {
            var referenceTypeId = _resolver.Resolve(
                reference.ReferenceType,
                reference.ReferenceTypeNamespace,
                NodeIdCategory.ReferenceType,
                manager.GetSystemContext(),
                manager.GetPredefinedNodes());

            if (referenceTypeId is null)
            {
                _logger.LogWarning(
                    "Unknown additional reference type '{ReferenceType}'. Skipping reference.",
                    reference.ReferenceType);
                continue;
            }

            var targetNodeId = reference.TargetNamespaceUri is not null
                ? NodeId.Create(reference.TargetNodeId, reference.TargetNamespaceUri, manager.GetSystemContext().NamespaceUris)
                : new NodeId(reference.TargetNodeId, namespaceIndex);

            node.AddReference(referenceTypeId, reference.IsForward, targetNodeId);
        }
    }

    private static void AddModellingRuleReference(NodeState node, OpcUaNodeConfiguration? nodeConfiguration)
    {
        if (nodeConfiguration?.ModellingRule is null or ModellingRule.Unset)
        {
            return;
        }

        var modellingRuleNodeId = GetModellingRuleNodeId(nodeConfiguration.ModellingRule.Value);
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
