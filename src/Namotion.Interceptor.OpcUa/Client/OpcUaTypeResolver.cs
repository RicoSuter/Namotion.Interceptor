using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<(string NamespaceUri, object Identifier), Type?> _typeCache = new();

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

    public static Type ClassifyObjectNode(IReadOnlyList<ReferenceDescription> children)
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

    public async Task<Dictionary<NodeId, Type?>> ResolveVariableTypesAsync(
        ISession session,
        IReadOnlyList<(NodeId NodeId, ReferenceDescription Reference)> variables,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<NodeId, Type?>(variables.Count);
        if (variables.Count == 0)
        {
            return result;
        }

        var uncachedVariables = new List<(NodeId NodeId, ReferenceDescription Reference)>(variables.Count);
        foreach (var (nodeId, reference) in variables)
        {
            var cacheKey = GetCacheKey(reference, session);
            if (_typeCache.TryGetValue(cacheKey, out var cachedType))
            {
                result[nodeId] = cachedType;
            }
            else
            {
                uncachedVariables.Add((nodeId, reference));
            }
        }

        if (uncachedVariables.Count == 0)
        {
            return result;
        }

        var maxReadIdsPerBatch = SessionBatchLimits.GetMaxNodesPerRead(session);

        var nodesToRead = new ReadValueIdCollection(uncachedVariables.Count * 2);
        foreach (var (nodeId, _) in uncachedVariables)
        {
            nodesToRead.Add(new ReadValueId { NodeId = nodeId, AttributeId = Opc.Ua.Attributes.DataType });
            nodesToRead.Add(new ReadValueId { NodeId = nodeId, AttributeId = Opc.Ua.Attributes.ValueRank });
        }

        var allResults = new DataValueCollection(nodesToRead.Count);
        await ReadBatchAsync(nodesToRead, 0, nodesToRead.Count, maxReadIdsPerBatch, allResults, session, cancellationToken).ConfigureAwait(false);

        for (var i = 0; i < uncachedVariables.Count; i++)
        {
            var (nodeId, reference) = uncachedVariables[i];
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

            var cacheKey = GetCacheKey(reference, session);
            _typeCache.TryAdd(cacheKey, type);
            result[nodeId] = type;
        }

        return result;
    }

    public static Type? TryMapBuiltInType(BuiltInType builtInType) => builtInType switch
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

    private async Task ReadBatchAsync(
        ReadValueIdCollection nodesToRead,
        int offset,
        int end,
        int maxBatchSize,
        DataValueCollection allResults,
        ISession session,
        CancellationToken cancellationToken)
    {
        for (var batchStart = offset; batchStart < end; batchStart += maxBatchSize)
        {
            var batchEnd = Math.Min(batchStart + maxBatchSize, end);
            var count = batchEnd - batchStart;
            var chunk = new ReadValueIdCollection(count);
            for (var i = batchStart; i < batchEnd; i++)
            {
                chunk.Add(nodesToRead[i]);
            }

            ReadResponse response;
            try
            {
                response = await session.ReadAsync(null, 0, TimestampsToReturn.Neither, chunk, cancellationToken).ConfigureAwait(false);
            }
            catch (ServiceResultException ex) when (count > 2 && SessionBatchLimits.IsBatchTooLarge(ex))
            {
                _logger.LogWarning(
                    "ReadAsync rejected batch of {Count} items ({StatusCode}). Splitting into smaller batches.",
                    count, ex.StatusCode);

                var halvedBatch = Math.Max(2, count / 2);
                await ReadBatchAsync(nodesToRead, batchStart, batchEnd, halvedBatch, allResults, session, cancellationToken).ConfigureAwait(false);
                continue;
            }

            allResults.AddRange(response.Results);
        }
    }

    private static (string NamespaceUri, object Identifier) GetCacheKey(ReferenceDescription reference, ISession session) =>
        (reference.NodeId.NamespaceUri ?? session.NamespaceUris.GetString(reference.NodeId.NamespaceIndex), reference.NodeId.Identifier);
}
