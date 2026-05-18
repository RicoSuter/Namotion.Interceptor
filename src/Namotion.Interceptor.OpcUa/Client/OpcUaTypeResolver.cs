using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Dynamic;
using Namotion.Interceptor.OpcUa.Attributes;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

public class OpcUaTypeResolver
{
    private readonly ILogger _logger;

    public OpcUaTypeResolver(ILogger logger)
    {
        _logger = logger;
    }

    public virtual Attribute[] GetDynamicPropertyAttributes(ReferenceDescription reference, ISession session)
    {
        var namespaceUri = reference.NodeId.NamespaceUri ?? session.NamespaceUris.GetString(reference.NodeId.NamespaceIndex);
        return
        [
            new OpcUaNodeAttribute(reference.BrowseName.Name, namespaceUri)
            {
                NodeIdentifier = reference.NodeId.Identifier.ToString(),
                NodeNamespaceUri = namespaceUri
            }
        ];
    }

    internal static Type ResolveObjectNodeType(IReadOnlyList<ReferenceDescription> children)
    {
        if (children.Count > 0 && children[0].NodeClass == NodeClass.Object)
        {
            var name = children[0].BrowseName?.Name;
            if (name is not null)
            {
                var bracketStart = name.LastIndexOf('[');
                if (bracketStart >= 0 && name.EndsWith("]"))
                {
                    var content = name.AsSpan(bracketStart + 1, name.Length - bracketStart - 2);
                    if (int.TryParse(content, out _))
                    {
                        return typeof(DynamicSubject[]);
                    }

                    return typeof(IReadOnlyDictionary<string, DynamicSubject>);
                }
            }
        }

        return typeof(DynamicSubject);
    }

    public virtual async Task<IReadOnlyDictionary<NodeId, Type?>> ResolveVariableTypesAsync(
        ISession session,
        IReadOnlyList<ReferenceDescription> variables,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<NodeId, Type?>(variables.Count);
        if (variables.Count == 0)
        {
            return result;
        }

        var resolvedVariables = new List<(NodeId NodeId, ReferenceDescription Reference)>(variables.Count);
        foreach (var reference in variables)
        {
            var nodeId = ExpandedNodeId.ToNodeId(reference.NodeId, session.NamespaceUris);
            if (nodeId is not null)
            {
                resolvedVariables.Add((nodeId, reference));
            }
        }

        if (resolvedVariables.Count == 0)
        {
            return result;
        }

        var nodesToRead = new ReadValueIdCollection(resolvedVariables.Count * 2);
        foreach (var (nodeId, _) in resolvedVariables)
        {
            nodesToRead.Add(new ReadValueId { NodeId = nodeId, AttributeId = Opc.Ua.Attributes.DataType });
            nodesToRead.Add(new ReadValueId { NodeId = nodeId, AttributeId = Opc.Ua.Attributes.ValueRank });
        }

        var allResults = await session.ReadNodesAsync(nodesToRead, _logger, cancellationToken).ConfigureAwait(false);

        for (var i = 0; i < resolvedVariables.Count; i++)
        {
            var (nodeId, reference) = resolvedVariables[i];
            var dataTypeIndex = i * 2;
            var valueRankIndex = dataTypeIndex + 1;

            Type? type = null;
            try
            {
                if (dataTypeIndex < allResults.Count && valueRankIndex < allResults.Count &&
                    StatusCode.IsGood(allResults[dataTypeIndex].StatusCode))
                {
                    var dataTypeId = allResults[dataTypeIndex].Value as NodeId;
                    if (dataTypeId is not null)
                    {
                        var builtIn = await TypeInfo.GetBuiltInTypeAsync(dataTypeId, session.TypeTree, cancellationToken).ConfigureAwait(false);
                        var elementType = TryMapBuiltInType(builtIn);
                        if (elementType is not null)
                        {
                            var valueRank = allResults[valueRankIndex].Value is int vr ? vr : -1;
                            type = valueRank >= 0 ? elementType.MakeArrayType() : elementType;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to infer CLR type for node {BrowseName}", reference.BrowseName.Name);
            }

            result[nodeId] = type;
        }

        return result;
    }

    private static Type? TryMapBuiltInType(BuiltInType builtInType) => builtInType switch
    {
        BuiltInType.Boolean => typeof(bool),
        BuiltInType.SByte => typeof(sbyte),
        BuiltInType.Byte => typeof(byte),
        BuiltInType.Int16 => typeof(short),
        BuiltInType.UInt16 => typeof(ushort),
        BuiltInType.Int32 => typeof(int),
        BuiltInType.UInt32 => typeof(uint),
        BuiltInType.Int64 => typeof(long),
        BuiltInType.UInt64 => typeof(ulong),
        BuiltInType.Float => typeof(float),
        BuiltInType.Double => typeof(double),
        BuiltInType.String => typeof(string),
        BuiltInType.DateTime => typeof(DateTime),
        BuiltInType.Guid => typeof(Guid),
        BuiltInType.ByteString => typeof(byte[]),
        BuiltInType.XmlElement => typeof(string),
        BuiltInType.NodeId => typeof(NodeId),
        BuiltInType.ExpandedNodeId => typeof(ExpandedNodeId),
        BuiltInType.StatusCode => typeof(StatusCode),
        BuiltInType.QualifiedName => typeof(QualifiedName),
        BuiltInType.LocalizedText => typeof(LocalizedText),
        BuiltInType.DiagnosticInfo => typeof(DiagnosticInfo),
        BuiltInType.ExtensionObject => typeof(ExtensionObject),
        BuiltInType.DataValue => typeof(DataValue),
        BuiltInType.Enumeration => typeof(int),
        BuiltInType.Number => typeof(double),
        BuiltInType.Integer => typeof(long),
        BuiltInType.UInteger => typeof(ulong),
        BuiltInType.Variant => null,
        BuiltInType.Null => null,
        _ => null
    };
}
