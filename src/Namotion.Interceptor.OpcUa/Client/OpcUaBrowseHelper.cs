using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Helper class for OPC UA browse operations.
/// </summary>
internal static class OpcUaBrowseHelper
{
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
