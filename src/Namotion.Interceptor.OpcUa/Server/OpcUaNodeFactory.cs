using Microsoft.Extensions.Logging;
using Namotion.Interceptor.OpcUa.Attributes;
using Namotion.Interceptor.OpcUa.Mapping;
using Opc.Ua;

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

    public NodeId GetNodeId(CustomNodeManager manager, OpcUaPropertyMapping? mapping, string fullPath)
    {
        var namespaceIndex = manager.NamespaceIndexes[0];

        if (mapping is { NodeIdentifier: not null })
        {
            if (mapping.NodeNamespaceUri is null)
                return new NodeId(mapping.NodeIdentifier, namespaceIndex);

            var nodeNamespaceIndex = ResolveNamespaceIndex(
                manager.GetSystemContext().NamespaceUris, mapping.NodeNamespaceUri,
                "NodeIdentifier namespace URI", mapping.NodeIdentifier);
            return new NodeId(mapping.NodeIdentifier, nodeNamespaceIndex);
        }

        return new NodeId(fullPath, namespaceIndex);
    }

    public QualifiedName GetBrowseName(CustomNodeManager manager, string name, OpcUaPropertyMapping? mapping, object? index)
    {
        var namespaceIndex = manager.NamespaceIndexes[0];

        if (mapping?.BrowseName is null)
        {
            return new QualifiedName(name + (index is not null ? $"[{index}]" : string.Empty), namespaceIndex);
        }

        if (mapping.BrowseNamespaceUri is not null)
        {
            var browseNamespaceIndex = ResolveNamespaceIndex(
                manager.GetSystemContext().NamespaceUris, mapping.BrowseNamespaceUri,
                "BrowseName namespace URI", mapping.BrowseName);
            return new QualifiedName(mapping.BrowseName, browseNamespaceIndex);
        }

        return new QualifiedName(mapping.BrowseName, namespaceIndex);
    }

    /// <summary>
    /// Resolves an OPC UA namespace URI to its registered index, failing fast with an actionable diagnostic
    /// when the URI is not in the namespace table. <c>NamespaceTable.GetIndex</c> returns -1 for an
    /// unregistered URI; without this guard a downstream cast or SDK call would either silently place the
    /// node in the wrong namespace or surface a generic error that does not name the offending node.
    /// </summary>
    /// <param name="namespaceUris">The server's namespace table.</param>
    /// <param name="namespaceUri">The URI to look up.</param>
    /// <param name="contextLabel">A short label describing the URI's role (e.g. "BrowseName namespace URI").</param>
    /// <param name="nodeLabel">The name or identifier of the node the URI belongs to, used in the error.</param>
    internal static ushort ResolveNamespaceIndex(
        NamespaceTable namespaceUris, string namespaceUri, string contextLabel, string nodeLabel)
    {
        var namespaceIndex = namespaceUris.GetIndex(namespaceUri);
        if (namespaceIndex < 0)
        {
            throw new InvalidOperationException(
                $"{contextLabel} '{namespaceUri}' for node '{nodeLabel}' " +
                $"is not registered in the server's namespace table.");
        }

        return (ushort)namespaceIndex;
    }

    public NodeId? GetReferenceTypeId(CustomNodeManager manager, OpcUaPropertyMapping? mapping)
    {
        if (mapping?.ReferenceType is null)
        {
            return null;
        }

        return _resolver.Resolve(
            mapping.ReferenceType,
            mapping.ReferenceTypeNamespace,
            NodeIdCategory.ReferenceType,
            manager.GetSystemContext(),
            manager.GetPredefinedNodes());
    }

    public NodeId? GetChildReferenceTypeId(CustomNodeManager manager, OpcUaPropertyMapping? mapping)
    {
        if (mapping?.ItemReferenceType is null)
        {
            return null;
        }

        return _resolver.Resolve(
            mapping.ItemReferenceType,
            mapping.ItemReferenceTypeNamespace,
            NodeIdCategory.ReferenceType,
            manager.GetSystemContext(),
            manager.GetPredefinedNodes());
    }

    public NodeId? GetTypeDefinitionId(CustomNodeManager manager, OpcUaPropertyMapping? mapping) =>
        GetTypeDefinitionIdCore(manager, mapping?.TypeDefinition, mapping?.TypeDefinitionNamespace);

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

    public NodeId? GetDataTypeOverride(CustomNodeManager manager, OpcUaPropertyMapping? mapping)
    {
        if (mapping?.DataType is null)
        {
            return null;
        }

        return _resolver.Resolve(
            mapping.DataType,
            mapping.DataTypeNamespace,
            NodeIdCategory.DataType,
            manager.GetSystemContext(),
            manager.GetPredefinedNodes());
    }

    public FolderState CreateFolderNode(
        CustomNodeManager manager,
        NodeId parentId, NodeId nodeId, QualifiedName browseName,
        NodeId? typeDefinition, NodeId? referenceType, OpcUaPropertyMapping? mapping)
    {
        var parentNode = manager.FindNode(parentId);

        var folderNode = new FolderState(parentNode)
        {
            NodeId = nodeId,
            BrowseName = browseName,
            DisplayName = new LocalizedText(mapping?.DisplayName ?? browseName.Name),
            Description = mapping?.Description is not null
                ? new LocalizedText(mapping.Description)
                : null,
            TypeDefinitionId = typeDefinition ?? ObjectTypeIds.FolderType,
            WriteMask = AttributeWriteMask.None,
            UserWriteMask = AttributeWriteMask.None,
            ReferenceTypeId = referenceType ?? ReferenceTypeIds.HasComponent
        };

        if (mapping?.EventNotifier is { } eventNotifier && eventNotifier != byte.MaxValue)
        {
            folderNode.EventNotifier = eventNotifier;
        }

        parentNode?.AddChild(folderNode);

        manager.AddNode(folderNode);
        AddModellingRuleReference(folderNode, mapping);
        return folderNode;
    }

    public BaseObjectState CreateObjectNode(
        CustomNodeManager manager,
        NodeId parentId, NodeId nodeId, QualifiedName browseName,
        NodeId? typeDefinition, NodeId? referenceType, OpcUaPropertyMapping? mapping)
    {
        var parentNode = manager.FindNode(parentId);

        var objectNode = new BaseObjectState(parentNode)
        {
            NodeId = nodeId,
            BrowseName = browseName,
            DisplayName = new LocalizedText(mapping?.DisplayName ?? browseName.Name),
            Description = mapping?.Description is not null
                ? new LocalizedText(mapping.Description)
                : null,
            TypeDefinitionId = typeDefinition ?? ObjectTypeIds.BaseObjectType,
            WriteMask = AttributeWriteMask.None,
            UserWriteMask = AttributeWriteMask.None,
            ReferenceTypeId = referenceType ?? ReferenceTypeIds.HasComponent
        };

        if (mapping?.EventNotifier is { } eventNotifier && eventNotifier != byte.MaxValue)
        {
            objectNode.EventNotifier = eventNotifier;
        }

        parentNode?.AddChild(objectNode);

        manager.AddNode(objectNode);
        AddModellingRuleReference(objectNode, mapping);
        return objectNode;
    }

    public BaseDataVariableState CreateVariableNode(
        CustomNodeManager manager,
        NodeId parentId, NodeId nodeId, QualifiedName browseName,
        Opc.Ua.TypeInfo dataType, NodeId? referenceType, NodeId? dataTypeOverride,
        OpcUaPropertyMapping? mapping)
    {
        var parentNode = manager.FindNode(parentId);

        var variable = new BaseDataVariableState(parentNode)
        {
            NodeId = nodeId,

            SymbolicName = browseName.Name,
            BrowseName = browseName,
            DisplayName = new LocalizedText(mapping?.DisplayName ?? browseName.Name),
            Description = mapping?.Description is not null
                ? new LocalizedText(mapping.Description)
                : null,

            TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
            DataType = dataTypeOverride ?? Opc.Ua.TypeInfo.GetDataTypeId(dataType),
            ValueRank = dataType.ValueRank,
            AccessLevel = AccessLevels.CurrentReadOrWrite,
            UserAccessLevel = AccessLevels.CurrentReadOrWrite,
            StatusCode = StatusCodes.Good,
            Timestamp = DateTime.UtcNow, // Default; overridden by caller with property write timestamp when available

            ReferenceTypeId = referenceType ?? ReferenceTypeIds.HasProperty
        };

        parentNode?.AddChild(variable);

        manager.AddNode(variable);
        AddModellingRuleReference(variable, mapping);
        return variable;
    }

    public void AddAdditionalReferences(CustomNodeManager manager, NodeState node, OpcUaPropertyMapping? mapping)
    {
        if (mapping?.AdditionalReferences is null)
        {
            return;
        }

        var namespaceIndex = manager.NamespaceIndexes[0];

        foreach (var reference in mapping.AdditionalReferences)
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

            NodeId targetNodeId;
            if (reference.TargetNamespaceUri is not null)
            {
                var targetNamespaceIndex = ResolveNamespaceIndex(
                    manager.GetSystemContext().NamespaceUris, reference.TargetNamespaceUri,
                    "AdditionalReference target namespace URI", reference.TargetNodeId);
                targetNodeId = new NodeId(reference.TargetNodeId, targetNamespaceIndex);
            }
            else
            {
                targetNodeId = new NodeId(reference.TargetNodeId, namespaceIndex);
            }

            node.AddReference(referenceTypeId, reference.IsForward, targetNodeId);
        }
    }

    private static void AddModellingRuleReference(NodeState node, OpcUaPropertyMapping? mapping)
    {
        if (mapping?.ModellingRule is null or ModellingRule.Unset)
        {
            return;
        }

        var modellingRuleNodeId = GetModellingRuleNodeId(mapping.ModellingRule.Value);
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
