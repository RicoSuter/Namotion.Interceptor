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

    public virtual async Task<Type?> TryGetTypeForNodeAsync(ISession session, ReferenceDescription reference, CancellationToken cancellationToken)
    {
        // Check cache first
        var cacheKey = (reference.NodeId.NamespaceUri ?? session.NamespaceUris.GetString(reference.NodeId.NamespaceIndex), reference.NodeId.Identifier);
        if (_typeCache.TryGetValue(cacheKey, out var cachedType))
        {
            return cachedType;
        }

        var type = await TryGetTypeForNodeWithoutCacheAsync(session, reference, cancellationToken).ConfigureAwait(false);
        _typeCache.TryAdd(cacheKey, type);
        return type;
    }

    private async Task<Type?> TryGetTypeForNodeWithoutCacheAsync(ISession session, ReferenceDescription reference, CancellationToken cancellationToken)
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
                    ResultMask = (uint)(BrowseResultMask.BrowseName | BrowseResultMask.NodeClass)
                }
            };

            // Browse only the first child: the [index] convention requires every collection/dictionary
            // element to follow the pattern, so the first reference is enough to classify the parent.
            var response = await session.BrowseAsync(null, null, 1u, browseDescriptions, cancellationToken);
            if (response.Results.Count > 0 &&
                response.Results[0].References.Count > 0 &&
                response.Results[0].References[0].NodeClass == NodeClass.Object)
            {
                var name = response.Results[0].References[0].BrowseName?.Name;
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
                    var builtIn = await TypeInfo.GetBuiltInTypeAsync(dataTypeId, session.TypeTree, cancellationToken);
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
