using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Dynamic;
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
    
    public async Task<Type> GetTypeForNodeAsync(Session session, ReferenceDescription reference, CancellationToken cancellationToken)
    {
        if (reference.NodeClass != NodeClass.Variable)
        {
            var (_, _ , nodeProperties, _) = await session.BrowseAsync(
                null,
                null,
                [(NodeId)reference.NodeId],
                0u,
                BrowseDirection.Forward,
                ReferenceTypeIds.HierarchicalReferences,
                true,
                (uint)NodeClass.Variable | (uint)NodeClass.Object,
                cancellationToken);

            if (nodeProperties.SelectMany(p => p).Any(n => n.NodeClass == NodeClass.Variable))
                return typeof(DynamicSubject);
            
            return typeof(DynamicSubject[]);
        }

        try
        {
            var nodeId = ExpandedNodeId.ToNodeId(reference.NodeId, session.NamespaceUris);
            if (nodeId is null)
            {
                return typeof(object);
            }

            var nodesToRead = new ReadValueIdCollection
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
                    var elementType = MapBuiltInType(builtIn);

                    // If ValueRank >= 0 we treat it as (at least) an array - simplification for multi-dim arrays.
                    var valueRank = response.Results[1].Value is int vr ? vr : -1; // -1 => scalar
                    if (valueRank >= 0)
                    {
                        try
                        {
                            return elementType.MakeArrayType();
                        }
                        catch
                        {
                            return typeof(object[]);
                        }
                    }

                    return elementType;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to infer CLR type for node {BrowseName}", reference.BrowseName.Name);
        }

        return typeof(object);
    }

    private static Type MapBuiltInType(BuiltInType builtInType) => builtInType switch
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
        _ => typeof(object)
    };
}