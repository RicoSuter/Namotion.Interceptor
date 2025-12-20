using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Dynamic;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

public class OpcUaTypeResolver
{
    private readonly ILogger _logger;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string NamespaceUri, object Identifier), Type?> _typeCache = new();

    public OpcUaTypeResolver(ILogger logger)
    {
        _logger = logger;
    }

    public virtual async Task<Type?> TryGetTypeForNodeAsync(ISession session, ReferenceDescription reference, CancellationToken cancellationToken)
    {
        // Check cache first
        var cacheKey = (reference.NodeId.NamespaceUri ?? session.NamespaceUris.GetString(reference.NodeId.NamespaceIndex), reference.NodeId.Identifier);
        if (_typeCache.TryGetValue(cacheKey, out var cachedType))
        {
            return cachedType;
        }

        var type = await TryGetTypeForNodeCoreAsync(session, reference, cancellationToken).ConfigureAwait(false);
        _typeCache.TryAdd(cacheKey, type);
        return type;
    }

    private async Task<Type?> TryGetTypeForNodeCoreAsync(ISession session, ReferenceDescription reference, CancellationToken cancellationToken)
    {
        var nodeId = ExpandedNodeId.ToNodeId(reference.NodeId, session.NamespaceUris);

        if (reference.NodeClass != NodeClass.Variable)
        {
            var browseDescriptions = new BrowseDescriptionCollection
            {
                new BrowseDescription
                {
                    NodeId = nodeId!,
                    BrowseDirection = BrowseDirection.Forward,
                    ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
                    IncludeSubtypes = true,
                    NodeClassMask = (uint)NodeClass.Variable | (uint)NodeClass.Object,
                    ResultMask = (uint)BrowseResultMask.All
                }
            };

            var response = await session.BrowseAsync(
                null,
                null,
                0u,
                browseDescriptions,
                cancellationToken);

            if (response.Results.Count > 0 && response.Results[0].References.Any(n => n.NodeClass == NodeClass.Variable))
            {
                return typeof(DynamicSubject);
            }

            return typeof(DynamicSubject[]);
        }

        try
        {
            if (nodeId is null)
            {
                return null;
            }

            var nodesToRead = new ReadValueIdCollection(2)
            {
                new ReadValueId { NodeId = nodeId, AttributeId = Opc.Ua.Attributes.DataType },
                new ReadValueId { NodeId = nodeId, AttributeId = Opc.Ua.Attributes.ValueRank }
            };

            var response = await session.ReadAsync(null, 0, TimestampsToReturn.Neither, nodesToRead, cancellationToken);
            if (response.Results.Count >= 2 && StatusCode.IsGood(response.Results[0].StatusCode))
            {
                var dataTypeId = response.Results[0].Value as NodeId;
                if (dataTypeId != null)
                {
                    var builtIn = TypeInfo.GetBuiltInType(dataTypeId);
                    var elementType = TryMapBuiltInType(builtIn);
                    if (elementType is not null)
                    {
                        // If ValueRank >= 0 we treat it as (at least) an array - simplification for multi-dim arrays.
                        var valueRank = response.Results[1].Value is int vr ? vr : -1; // -1 => scalar
                        if (valueRank >= 0)
                        {
                            return elementType.MakeArrayType();
                        }

                        return elementType;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to infer CLR type for node {BrowseName}", reference.BrowseName.Name);
        }

        return null;
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
        BuiltInType.NodeId => typeof(NodeId),
        BuiltInType.ExpandedNodeId => typeof(ExpandedNodeId),
        BuiltInType.StatusCode => typeof(StatusCode),
        BuiltInType.QualifiedName => typeof(QualifiedName),
        BuiltInType.LocalizedText => typeof(LocalizedText),
        BuiltInType.DiagnosticInfo => typeof(DiagnosticInfo),
        BuiltInType.DataValue => typeof(DataValue),
        BuiltInType.Enumeration => typeof(int), // map enum to underlying Int32
        _ => null
    };
}