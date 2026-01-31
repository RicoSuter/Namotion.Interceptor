using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Graph;

/// <summary>
/// Helper class for OPC UA browse operations.
/// </summary>
internal static class OpcUaBrowseHelper
{
    // TODO: Move to ../ (ie root of project), not only used by graph features/classes

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
    /// </summary>
    /// <param name="browseName">The browse name to parse (e.g., "People[3]").</param>
    /// <param name="propertyName">The expected property name prefix (e.g., "People").</param>
    /// <param name="index">The parsed index if successful.</param>
    /// <returns>True if the browse name matches the expected format and index was parsed.</returns>
    public static bool TryParseCollectionIndex(string browseName, string? propertyName, out int index)
    {
        // TODO: Add tests for this method

        index = -1;

        if (propertyName is null)
        {
            return false;
        }

        // Expected format: "PropertyName[index]"
        var prefix = propertyName + "[";
        if (!browseName.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = "]";
        if (!browseName.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var indexStr = browseName.Substring(prefix.Length, browseName.Length - prefix.Length - suffix.Length);
        return int.TryParse(indexStr, out index);
    }

    /// <summary>
    /// Browses inverse (parent) references of a given node.
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
        var browseDescription = new BrowseDescriptionCollection
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

        var response = await session.BrowseAsync(
            null, null, 0, browseDescription, cancellationToken).ConfigureAwait(false);

        if (response.Results.Count > 0 && StatusCode.IsGood(response.Results[0].StatusCode))
        {
            return response.Results[0].References;
        }

        return [];
    }

    /// <summary>
    /// Browses child nodes of a given node.
    /// </summary>
    /// <param name="session">The OPC UA session.</param>
    /// <param name="nodeId">The node to browse.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The collection of child references.</returns>
    public static async Task<ReferenceDescriptionCollection> BrowseNodeAsync(
        ISession session,
        NodeId nodeId,
        CancellationToken cancellationToken)
    {
        const uint nodeClassMask = (uint)NodeClass.Variable | (uint)NodeClass.Object;

        var browseDescription = new BrowseDescriptionCollection
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

        var response = await session.BrowseAsync(
            null,
            null,
            0,
            browseDescription,
            cancellationToken).ConfigureAwait(false);

        if (response.Results.Count > 0 && StatusCode.IsGood(response.Results[0].StatusCode))
        {
            return response.Results[0].References;
        }

        return [];
    }
}
