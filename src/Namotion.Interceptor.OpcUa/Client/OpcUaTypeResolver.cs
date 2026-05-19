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

    /// <summary>
    /// Heuristically classifies an OPC UA Object node as an array, a string-keyed dictionary,
    /// or a single subject reference, based on the BrowseName of its first Object child.
    /// </summary>
    /// <remarks>
    /// <para>The convention is:
    /// <c>Name[number]</c> → <see cref="DynamicSubject"/>[];
    /// <c>Name[token]</c> with a non-empty non-numeric token → <see cref="IReadOnlyDictionary{TKey,TValue}"/>;
    /// otherwise (no brackets, empty <c>[]</c>, or non-Object first child) → single
    /// <see cref="DynamicSubject"/> reference.</para>
    /// <para><strong>Warning: server-order dependent.</strong> Only the first Object child
    /// is inspected. OPC UA does not guarantee a stable browse order across servers (or
    /// across reconnects to the same server), so a parent with mixed bracket patterns
    /// (e.g. both <c>Items[0]</c> and <c>Items[Key]</c>) can classify as array on one
    /// server and dictionary on another. Override this method to implement a strategy
    /// that scans all children if you need cross-server determinism.</para>
    /// </remarks>
    public virtual Type ResolveObjectNodeType(IReadOnlyList<ReferenceDescription> children)
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
                    if (content.Length == 0)
                    {
                        // Empty brackets carry no key/index information; treat as a single reference.
                        return typeof(DynamicSubject);
                    }
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

        // ReadNodesAsync pads short responses and clamps long ones, so
        // `allResults.Count == resolvedVariables.Count * 2` and `allResults[i]` is
        // positionally aligned with `nodesToRead[i]`.
        var allResults = await session.ReadNodesAsync(nodesToRead, TimestampsToReturn.Neither, _logger, cancellationToken).ConfigureAwait(false);

        for (var i = 0; i < resolvedVariables.Count; i++)
        {
            var (nodeId, reference) = resolvedVariables[i];
            var dataTypeIndex = i * 2;
            var valueRankIndex = dataTypeIndex + 1;

            Type? type = null;
            try
            {
                if (!StatusCode.IsGood(allResults[dataTypeIndex].StatusCode))
                {
                    _logger.LogWarning("Failed to read DataType for node {BrowseName} ({StatusCode}).",
                        reference.BrowseName.Name, allResults[dataTypeIndex].StatusCode);
                }
                else if (allResults[dataTypeIndex].Value is NodeId dataTypeId)
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
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to infer CLR type for node {BrowseName}.", reference.BrowseName.Name);
            }

            result[nodeId] = type;
        }

        return result;
    }

    protected virtual Type? TryMapBuiltInType(BuiltInType builtInType) => builtInType switch
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
