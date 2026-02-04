using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa;

/// <summary>
/// Helper class for OPC UA browse operations.
/// </summary>
internal static class OpcUaHelper
{
    /// <summary>
    /// Finds a child node by browse name under a parent node.
    /// </summary>
    /// <param name="session">The OPC UA session.</param>
    /// <param name="parentNodeId">The parent node to search under.</param>
    /// <param name="browseName">The browse name of the child to find.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The NodeId of the child if found, otherwise null.</returns>
    public static async Task<NodeId?> FindChildNodeIdAsync(
        ISession session,
        NodeId parentNodeId,
        string browseName,
        CancellationToken cancellationToken)
    {
        var children = await BrowseNodeAsync(session, parentNodeId, cancellationToken).ConfigureAwait(false);
        foreach (var child in children)
        {
            if (child.BrowseName.Name == browseName)
            {
                return ExpandedNodeId.ToNodeId(child.NodeId, session.NamespaceUris);
            }
        }
        return null;
    }

    /// <summary>
    /// Finds the first parent node of a given node by browsing inverse references.
    /// </summary>
    /// <param name="session">The OPC UA session.</param>
    /// <param name="childNodeId">The child node to find the parent for.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The NodeId of the first parent if found, otherwise null.</returns>
    public static async Task<NodeId?> FindParentNodeIdAsync(
        ISession session,
        NodeId childNodeId,
        CancellationToken cancellationToken)
    {
        var parentRefs = await BrowseInverseReferencesAsync(session, childNodeId, cancellationToken).ConfigureAwait(false);
        if (parentRefs.Count > 0)
        {
            return ExpandedNodeId.ToNodeId(parentRefs[0].NodeId, session.NamespaceUris);
        }
        return null;
    }

    /// <summary>
    /// Reads node attributes to construct a ReferenceDescription with node details.
    /// </summary>
    /// <param name="session">The OPC UA session.</param>
    /// <param name="nodeId">The node to read details for.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A ReferenceDescription with the node's attributes, or null if read failed.</returns>
    public static async Task<ReferenceDescription?> ReadNodeDetailsAsync(
        ISession session,
        NodeId nodeId,
        CancellationToken cancellationToken)
    {
        var readValues = new ReadValueIdCollection
        {
            new ReadValueId { NodeId = nodeId, AttributeId = Opc.Ua.Attributes.BrowseName },
            new ReadValueId { NodeId = nodeId, AttributeId = Opc.Ua.Attributes.DisplayName },
            new ReadValueId { NodeId = nodeId, AttributeId = Opc.Ua.Attributes.NodeClass }
        };

        var response = await session.ReadAsync(
            null,
            0,
            TimestampsToReturn.Neither,
            readValues,
            cancellationToken).ConfigureAwait(false);

        if (response.Results.Count >= 3 &&
            StatusCode.IsGood(response.Results[0].StatusCode) &&
            StatusCode.IsGood(response.Results[2].StatusCode))
        {
            return new ReferenceDescription
            {
                NodeId = new ExpandedNodeId(nodeId),
                BrowseName = response.Results[0].Value as QualifiedName ?? new QualifiedName("Unknown"),
                DisplayName = response.Results[1].Value as LocalizedText ?? new LocalizedText("Unknown"),
                NodeClass = (NodeClass)(int)response.Results[2].Value
            };
        }

        return null;
    }

    /// <summary>
    /// Tries to parse a collection index from a browse name like "PropertyName[3]".
    /// Returns the base property name and index.
    /// </summary>
    /// <param name="browseName">The browse name to parse (e.g., "Sensors[3]").</param>
    /// <param name="baseName">The base property name if successful (e.g., "Sensors").</param>
    /// <param name="index">The parsed index if successful.</param>
    /// <returns>True if the browse name matches the collection index format.</returns>
    public static bool TryParseCollectionIndex(string browseName, out string? baseName, out int index)
    {
        baseName = null;
        index = -1;

        if (string.IsNullOrEmpty(browseName))
        {
            return false;
        }

        var openBracket = browseName.LastIndexOf('[');
        var closeBracket = browseName.LastIndexOf(']');

        if (openBracket <= 0 || closeBracket != browseName.Length - 1 || closeBracket <= openBracket + 1)
        {
            return false;
        }

        var indexStr = browseName.Substring(openBracket + 1, closeBracket - openBracket - 1);
        if (!int.TryParse(indexStr, out index) || index < 0)
        {
            index = -1;
            return false;
        }

        baseName = browseName.Substring(0, openBracket);
        return true;
    }

    /// <summary>
    /// Tries to parse a collection index from a browse name like "PropertyName[3]",
    /// validating that the base name matches the expected property name.
    /// </summary>
    /// <param name="browseName">The browse name to parse (e.g., "People[3]").</param>
    /// <param name="propertyName">The expected property name prefix (e.g., "People").</param>
    /// <param name="index">The parsed index if successful.</param>
    /// <returns>True if the browse name matches the expected format and index was parsed.</returns>
    public static bool TryParseCollectionIndex(string browseName, string? propertyName, out int index)
    {
        index = -1;

        if (propertyName is null)
        {
            return false;
        }

        if (TryParseCollectionIndex(browseName, out var baseName, out index))
        {
            return baseName == propertyName;
        }

        return false;
    }

    /// <summary>
    /// Reindexes the first occurrence of a collection index in a NodeId string.
    /// For example, "Root.Collection[2].Items[2]" with oldIndex=2, newIndex=1
    /// returns "Root.Collection[1].Items[2]" (only the FIRST [2] is replaced).
    /// </summary>
    /// <param name="nodeIdStr">The NodeId string to reindex.</param>
    /// <param name="oldIndex">The old index to find.</param>
    /// <param name="newIndex">The new index to replace with.</param>
    /// <returns>The reindexed string, or null if the index pattern was not found.</returns>
    public static string? ReindexFirstCollectionIndex(string nodeIdStr, int oldIndex, int newIndex)
    {
        if (string.IsNullOrEmpty(nodeIdStr))
        {
            return null;
        }

        var indexPattern = $"[{oldIndex}]";
        var firstOccurrence = nodeIdStr.IndexOf(indexPattern, StringComparison.Ordinal);
        if (firstOccurrence < 0)
        {
            return null;
        }

        // Replace only the first occurrence
        var newIndexPattern = $"[{newIndex}]";
        return string.Concat(
            nodeIdStr.AsSpan(0, firstOccurrence),
            newIndexPattern,
            nodeIdStr.AsSpan(firstOccurrence + indexPattern.Length));
    }

    /// <summary>
    /// Browses inverse (parent) references of a given node, handling continuation points for paginated results.
    /// </summary>
    /// <param name="session">The OPC UA session.</param>
    /// <param name="nodeId">The node to browse.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The collection of parent references.</returns>
    public static async Task<ReferenceDescriptionCollection> BrowseInverseReferencesAsync(
        ISession session,
        NodeId nodeId,
        CancellationToken cancellationToken)
    {
        var browseDescriptions = new BrowseDescriptionCollection
        {
            new BrowseDescription
            {
                NodeId = nodeId,
                BrowseDirection = BrowseDirection.Inverse,
                ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
                IncludeSubtypes = true,
                NodeClassMask = (uint)(NodeClass.Object | NodeClass.Variable),
                ResultMask = (uint)BrowseResultMask.All
            }
        };

        var results = new ReferenceDescriptionCollection();

        var response = await session.BrowseAsync(
            null, null, 0, browseDescriptions, cancellationToken).ConfigureAwait(false);

        if (response.Results.Count > 0 && StatusCode.IsGood(response.Results[0].StatusCode))
        {
            results.AddRange(response.Results[0].References);

            var continuationPoint = response.Results[0].ContinuationPoint;
            while (continuationPoint is { Length: > 0 })
            {
                var nextResponse = await session.BrowseNextAsync(
                    null, false,
                    [continuationPoint], cancellationToken).ConfigureAwait(false);

                if (nextResponse.Results.Count > 0 && StatusCode.IsGood(nextResponse.Results[0].StatusCode))
                {
                    var browseResult = nextResponse.Results[0];
                    if (browseResult.References is { Count: > 0 } nextReferences)
                    {
                        foreach (var reference in nextReferences)
                        {
                            results.Add(reference);
                        }
                    }
                    continuationPoint = browseResult.ContinuationPoint;
                }
                else
                {
                    break;
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Browses child nodes of a given node, handling continuation points for paginated results.
    /// </summary>
    /// <param name="session">The OPC UA session.</param>
    /// <param name="nodeId">The node to browse.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="maxReferencesPerNode">Maximum references per node (0 = use server default).</param>
    /// <returns>The collection of child references.</returns>
    public static async Task<ReferenceDescriptionCollection> BrowseNodeAsync(
        ISession session,
        NodeId nodeId,
        CancellationToken cancellationToken,
        uint maxReferencesPerNode = 0)
    {
        const uint nodeClassMask = (uint)NodeClass.Variable | (uint)NodeClass.Object;

        var browseDescriptions = new BrowseDescriptionCollection
        {
            new BrowseDescription
            {
                NodeId = nodeId,
                BrowseDirection = BrowseDirection.Forward,
                ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
                IncludeSubtypes = true,
                NodeClassMask = nodeClassMask,
                ResultMask = (uint)BrowseResultMask.All
            }
        };

        var results = new ReferenceDescriptionCollection();

        var response = await session.BrowseAsync(
            null,
            null,
            maxReferencesPerNode,
            browseDescriptions,
            cancellationToken).ConfigureAwait(false);

        if (response.Results.Count > 0 && StatusCode.IsGood(response.Results[0].StatusCode))
        {
            results.AddRange(response.Results[0].References);

            var continuationPoint = response.Results[0].ContinuationPoint;
            while (continuationPoint is { Length: > 0 })
            {
                var nextResponse = await session.BrowseNextAsync(
                    null, false,
                    [continuationPoint], cancellationToken).ConfigureAwait(false);

                if (nextResponse.Results.Count > 0 && StatusCode.IsGood(nextResponse.Results[0].StatusCode))
                {
                    var browseResult = nextResponse.Results[0];
                    if (browseResult.References is { Count: > 0 } nextReferences)
                    {
                        foreach (var reference in nextReferences)
                        {
                            results.Add(reference);
                        }
                    }
                    continuationPoint = browseResult.ContinuationPoint;
                }
                else
                {
                    break;
                }
            }
        }

        return results;
    }
}
