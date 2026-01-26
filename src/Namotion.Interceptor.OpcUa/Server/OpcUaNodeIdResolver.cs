using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Server;

/// <summary>
/// Unified service for resolving string identifiers to OPC UA NodeIds.
/// Supports multiple input formats: standard type names, NodeId strings, and BrowseNames with namespaces.
/// Results are cached for performance.
/// </summary>
internal sealed class OpcUaNodeIdResolver
{
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<(string Identifier, string? NamespaceUri, NodeIdCategory Category), NodeId?> _cache = new();

    // Standard type lookups built via reflection from OPC UA SDK types
    private static readonly Lazy<Dictionary<string, NodeId>> ObjectTypeLookup =
        new(() => BuildNodeIdLookup(typeof(ObjectTypeIds)));

    private static readonly Lazy<Dictionary<string, NodeId>> VariableTypeLookup =
        new(() => BuildNodeIdLookup(typeof(VariableTypeIds)));

    private static readonly Lazy<Dictionary<string, NodeId>> ReferenceTypeLookup =
        new(() => BuildNodeIdLookup(typeof(ReferenceTypeIds)));

    private static readonly Lazy<Dictionary<string, NodeId>> DataTypeLookup =
        new(() => BuildNodeIdLookup(typeof(DataTypeIds)));

    public OpcUaNodeIdResolver(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Resolves a string identifier to a NodeId using cascading resolution strategy:
    /// 1. Cache lookup (instant if previously resolved)
    /// 2. BrowseName lookup in predefined nodes (if namespace provided)
    /// 3. NodeId/ExpandedNodeId string parsing
    /// 4. Standard type lookup by category
    /// </summary>
    /// <param name="identifier">The identifier to resolve (type name, NodeId string, or BrowseName).</param>
    /// <param name="namespaceUri">Optional namespace URI for BrowseName lookup.</param>
    /// <param name="category">The category of node being resolved.</param>
    /// <param name="context">The system context for namespace resolution.</param>
    /// <param name="predefinedNodes">The predefined nodes dictionary for BrowseName lookup.</param>
    /// <returns>The resolved NodeId, or null if not found.</returns>
    public NodeId? Resolve(
        string? identifier,
        string? namespaceUri,
        NodeIdCategory category,
        ISystemContext context,
        NodeIdDictionary<NodeState> predefinedNodes)
    {
        if (string.IsNullOrEmpty(identifier))
        {
            return null;
        }

        var cacheKey = (identifier, namespaceUri, category);
        return _cache.GetOrAdd(cacheKey, _ =>
            ResolveCore(identifier, namespaceUri, category, context, predefinedNodes));
    }

    private NodeId? ResolveCore(
        string identifier,
        string? namespaceUri,
        NodeIdCategory category,
        ISystemContext context,
        NodeIdDictionary<NodeState> predefinedNodes)
    {
        // Strategy 1: If namespace provided, do BrowseName lookup
        if (!string.IsNullOrEmpty(namespaceUri))
        {
            var result = ResolveBrowseName(identifier, namespaceUri, context, predefinedNodes);
            if (result != null)
            {
                return result;
            }
            // Fall through to try other strategies
        }

        // Strategy 2: Try parsing as NodeId string (ns=X;i=Y or ns=X;s=Name)
        if (NodeId.TryParse(identifier, out var parsedNodeId))
        {
            return parsedNodeId;
        }

        // Strategy 3: Try parsing as ExpandedNodeId string (nsu=http://...;i=Y)
        if (ExpandedNodeId.TryParse(identifier, out var expandedNodeId) &&
            !string.IsNullOrEmpty(expandedNodeId.NamespaceUri))
        {
            var namespaceIndex = context.NamespaceUris.GetIndexOrAppend(expandedNodeId.NamespaceUri);
            return new NodeId(expandedNodeId.Identifier, namespaceIndex);
        }

        // Strategy 4: Standard type lookup by category
        var standardResult = ResolveStandardType(identifier, category);
        if (standardResult != null)
        {
            return standardResult;
        }

        // Not found - log warning (only on first resolution, cache prevents spam)
        LogResolutionFailure(identifier, namespaceUri, category);
        return null;
    }

    private NodeId? ResolveBrowseName(
        string browseName,
        string namespaceUri,
        ISystemContext context,
        NodeIdDictionary<NodeState> predefinedNodes)
    {
        var namespaceIndex = context.NamespaceUris.GetIndex(namespaceUri);
        if (namespaceIndex < 0)
        {
            return null;
        }

        var qualifiedName = new QualifiedName(browseName, (ushort)namespaceIndex);

        foreach (var node in predefinedNodes.Values)
        {
            if (node.BrowseName == qualifiedName)
            {
                return node.NodeId;
            }
        }

        return null;
    }

    private NodeId? ResolveStandardType(string identifier, NodeIdCategory category)
    {
        var lookup = category switch
        {
            NodeIdCategory.ObjectType => ObjectTypeLookup.Value,
            NodeIdCategory.VariableType => VariableTypeLookup.Value,
            NodeIdCategory.ReferenceType => ReferenceTypeLookup.Value,
            NodeIdCategory.DataType => DataTypeLookup.Value,
            NodeIdCategory.Node => null,
            _ => null
        };

        if (lookup != null && lookup.TryGetValue(identifier, out var nodeId))
        {
            return nodeId;
        }

        return null;
    }

    private void LogResolutionFailure(string identifier, string? namespaceUri, NodeIdCategory category)
    {
        if (!string.IsNullOrEmpty(namespaceUri))
        {
            _logger.LogWarning(
                "Could not resolve {Category} '{Identifier}' in namespace '{Namespace}'. " +
                "Verify the nodeset is loaded and the BrowseName is correct.",
                category, identifier, namespaceUri);
        }
        else
        {
            _logger.LogWarning(
                "Unknown {Category} '{Identifier}'. Expected: standard type name, " +
                "NodeId string (ns=X;i=Y), or BrowseName with namespace.",
                category, identifier);
        }
    }

    private static Dictionary<string, NodeId> BuildNodeIdLookup(Type typeIdsClass)
    {
        var lookup = new Dictionary<string, NodeId>(StringComparer.Ordinal);
        var fields = typeIdsClass.GetFields(BindingFlags.Public | BindingFlags.Static);

        foreach (var field in fields)
        {
            if (field.FieldType == typeof(NodeId) && field.GetValue(null) is NodeId nodeId)
            {
                lookup[field.Name] = nodeId;
            }
        }

        return lookup;
    }
}
