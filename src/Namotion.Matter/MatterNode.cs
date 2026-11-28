namespace Namotion.Matter;

/// <summary>A commissioned node with all attribute values the controller holds for it.</summary>
/// <remarks>Record equality compares <see cref="Attributes"/> and <see cref="EndpointParents"/> by reference, not by content.</remarks>
/// <param name="NodeId">The node ID.</param>
/// <param name="Available">Whether the node is reachable and its first report was applied.</param>
/// <param name="IsBridge">Whether the node is a bridge.</param>
/// <param name="DateCommissioned">When the node was commissioned, or null when unknown.</param>
/// <param name="LastInterview">When the node was last interviewed, or null when unknown.</param>
/// <param name="Attributes">The attribute values by path.</param>
/// <param name="EndpointParents">The direct parent endpoint of each endpoint other than 0 whose parent is known.</param>
public sealed record MatterNode(
    ulong NodeId,
    bool Available,
    bool IsBridge,
    DateTimeOffset? DateCommissioned,
    DateTimeOffset? LastInterview,
    IReadOnlyDictionary<AttributePath, MatterValue> Attributes,
    IReadOnlyDictionary<ushort, ushort> EndpointParents);
