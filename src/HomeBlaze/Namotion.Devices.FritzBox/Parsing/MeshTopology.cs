using System.Globalization;

namespace Namotion.Devices.FritzBox.Parsing;

/// <summary>
/// A meshed FRITZ! device: the router itself, a FRITZ!Repeater or another FRITZ!Box in the mesh. <c>MacAddress</c>
/// is the device address, normalized, and the key of the access point. <c>MacAddresses</c> holds every address of
/// the device and its interfaces, normalized. <c>UplinkSpeed</c> is the current speed of the link towards the
/// router in Mbit/s.
/// </summary>
internal sealed record AccessPointInfo(
    string MacAddress,
    IReadOnlySet<string> MacAddresses,
    string? Name,
    string? Model,
    string? Manufacturer,
    string? SoftwareVersion,
    bool IsMaster,
    bool IsConnected,
    string? UplinkType,
    decimal? UplinkSpeed);

/// <summary>
/// Where a connected client is attached. <c>Band</c> is the WLAN band, null for a wired client. <c>Port</c> is the
/// LAN port, null for a wireless client or when the router does not name it. <c>LinkSpeed</c> is the current link
/// speed in Mbit/s.
/// </summary>
internal sealed record ClientPosition(
    string AccessPointMacAddress,
    string? AccessPointName,
    string? Band,
    int? Port,
    decimal? LinkSpeed,
    bool IsWireless);

/// <summary>
/// Who is connected where, built from the mesh list.
/// </summary>
internal sealed record MeshTopology(
    IReadOnlyList<AccessPointInfo> AccessPoints,
    IReadOnlyDictionary<string, ClientPosition> ClientPositions,
    IReadOnlySet<string> MeshMacAddresses)
{
    private const string ConnectedState = "CONNECTED";
    private const string UplinkInterfacePrefix = "UPLINK";

    // A link from one node's own side, and the node reached across it.
    private readonly struct MeshLinkCandidate
    {
        internal MeshLinkCandidate(MeshNode otherNode, MeshNodeInterface ownInterface, MeshNodeLink link)
        {
            OtherNode = otherNode;
            OwnInterface = ownInterface;
            Link = link;
        }

        internal MeshNode OtherNode { get; }

        internal MeshNodeInterface OwnInterface { get; }

        internal MeshNodeLink Link { get; }
    }

    internal static MeshTopology Build(MeshList meshList)
    {
        var nodes = meshList.Nodes ?? [];
        var interfaces = new Dictionary<string, (MeshNode Node, MeshNodeInterface Interface)>(StringComparer.Ordinal);
        var links = new Dictionary<string, MeshNodeLink>(StringComparer.Ordinal);
        var meshMacAddresses = new HashSet<string>(StringComparer.Ordinal);
        var nodeMacAddresses = new Dictionary<MeshNode, HashSet<string>>(ReferenceEqualityComparer.Instance);

        foreach (var node in nodes)
        {
            RegisterInterfaces(node, interfaces, links);
            if (IsMeshNode(node))
            {
                var addresses = CollectNodeAddresses(node);
                nodeMacAddresses[node] = addresses;
                meshMacAddresses.UnionWith(addresses);
            }
        }

        var uplinks = ResolveUplinks(nodes, CollectMeshLinkCandidates(links, interfaces));
        var positions = BuildClientPositions(links, interfaces);
        var accessPoints = BuildAccessPoints(nodes, nodeMacAddresses, uplinks);

        return new MeshTopology(accessPoints, positions, meshMacAddresses);
    }

    private static void RegisterInterfaces(
        MeshNode node,
        Dictionary<string, (MeshNode Node, MeshNodeInterface Interface)> interfaces,
        Dictionary<string, MeshNodeLink> links)
    {
        foreach (var nodeInterface in node.Interfaces ?? [])
        {
            if (nodeInterface.Uid is { } interfaceUid)
            {
                interfaces.TryAdd(interfaceUid, (node, nodeInterface));
            }

            CollectLinks(nodeInterface, links);
        }
    }

    private static void CollectLinks(MeshNodeInterface nodeInterface, Dictionary<string, MeshNodeLink> links)
    {
        foreach (var link in nodeInterface.Links ?? [])
        {
            if (link.Uid is { } linkUid)
            {
                links.TryAdd(linkUid, link);
            }
        }
    }

    private static HashSet<string> CollectNodeAddresses(MeshNode node)
    {
        var addresses = new HashSet<string>(StringComparer.Ordinal);
        AddAddress(addresses, node.MacAddress);
        foreach (var nodeInterface in node.Interfaces ?? [])
        {
            AddAddress(addresses, nodeInterface.MacAddress);
        }

        return addresses;
    }

    // One entry per node that has at least one connected link to another mesh node, listing every such link from
    // that node's own side.
    private static Dictionary<MeshNode, List<MeshLinkCandidate>> CollectMeshLinkCandidates(
        Dictionary<string, MeshNodeLink> links,
        Dictionary<string, (MeshNode Node, MeshNodeInterface Interface)> interfaces)
    {
        var candidates = new Dictionary<MeshNode, List<MeshLinkCandidate>>(ReferenceEqualityComparer.Instance);
        foreach (var link in links.Values)
        {
            if (!TryGetConnectedEndpoints(link, interfaces, out var first, out var second))
            {
                continue;
            }

            if (!IsMeshNode(first.Node) || !IsMeshNode(second.Node))
            {
                continue;
            }

            AddCandidate(candidates, first.Node, first.Interface, second.Node, link);
            AddCandidate(candidates, second.Node, second.Interface, first.Node, link);
        }

        return candidates;
    }

    private static void AddCandidate(
        Dictionary<MeshNode, List<MeshLinkCandidate>> candidates,
        MeshNode node,
        MeshNodeInterface ownInterface,
        MeshNode otherNode,
        MeshNodeLink link)
    {
        if (!candidates.TryGetValue(node, out var list))
        {
            list = [];
            candidates[node] = list;
        }

        list.Add(new MeshLinkCandidate(otherNode, ownInterface, link));
    }

    private static Dictionary<MeshNode, MeshNodeLink> ResolveUplinks(
        List<MeshNode> nodes,
        Dictionary<MeshNode, List<MeshLinkCandidate>> candidatesByNode)
    {
        var uplinks = new Dictionary<MeshNode, MeshNodeLink>(ReferenceEqualityComparer.Instance);
        foreach (var node in nodes)
        {
            if (IsMaster(node) || !candidatesByNode.TryGetValue(node, out var candidates))
            {
                continue;
            }

            uplinks[node] = SelectUplink(candidates);
        }

        return uplinks;
    }

    /// <summary>
    /// Picks a node's uplink by priority rather than by the order links happen to appear in the document.
    /// </summary>
    /// <returns>
    /// The link towards the router: one whose other side is the master, otherwise one on an interface named
    /// <c>UPLINK...</c>, otherwise the first connected mesh link, in that priority order.
    /// </returns>
    private static MeshNodeLink SelectUplink(List<MeshLinkCandidate> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (IsMaster(candidate.OtherNode))
            {
                return candidate.Link;
            }
        }

        foreach (var candidate in candidates)
        {
            if (candidate.OwnInterface.Name?.StartsWith(UplinkInterfacePrefix, StringComparison.OrdinalIgnoreCase) == true)
            {
                return candidate.Link;
            }
        }

        return candidates[0].Link;
    }

    private static Dictionary<string, ClientPosition> BuildClientPositions(
        Dictionary<string, MeshNodeLink> links,
        Dictionary<string, (MeshNode Node, MeshNodeInterface Interface)> interfaces)
    {
        var positions = new Dictionary<string, ClientPosition>(StringComparer.Ordinal);
        foreach (var link in links.Values)
        {
            if (!TryGetConnectedEndpoints(link, interfaces, out var first, out var second))
            {
                continue;
            }

            var firstIsMeshNode = IsMeshNode(first.Node);
            var secondIsMeshNode = IsMeshNode(second.Node);
            if (!firstIsMeshNode && !secondIsMeshNode)
            {
                continue;
            }

            if (firstIsMeshNode && secondIsMeshNode)
            {
                continue;
            }

            AddClientPosition(positions, link, first, second, firstIsMeshNode);
        }

        return positions;
    }

    /// <returns>
    /// Whether the link is connected and both of its interfaces are known. The matching nodes are returned through
    /// <paramref name="first"/> and <paramref name="second"/>.
    /// </returns>
    private static bool TryGetConnectedEndpoints(
        MeshNodeLink link,
        Dictionary<string, (MeshNode Node, MeshNodeInterface Interface)> interfaces,
        out (MeshNode Node, MeshNodeInterface Interface) first,
        out (MeshNode Node, MeshNodeInterface Interface) second)
    {
        first = default;
        second = default;
        if (!string.Equals(link.State, ConnectedState, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (link.InterfaceUid1 is null || link.InterfaceUid2 is null)
        {
            return false;
        }

        return interfaces.TryGetValue(link.InterfaceUid1, out first) && interfaces.TryGetValue(link.InterfaceUid2, out second);
    }

    private static void AddClientPosition(
        Dictionary<string, ClientPosition> positions,
        MeshNodeLink link,
        (MeshNode Node, MeshNodeInterface Interface) first,
        (MeshNode Node, MeshNodeInterface Interface) second,
        bool firstIsAccessPoint)
    {
        var (accessPoint, client) = firstIsAccessPoint ? (first, second) : (second, first);
        var clientMacAddress = MacAddresses.Normalize(client.Interface.MacAddress) ?? MacAddresses.Normalize(client.Node.MacAddress);
        var accessPointMacAddress = MacAddresses.Normalize(accessPoint.Node.MacAddress);
        if (clientMacAddress is null || accessPointMacAddress is null)
        {
            return;
        }

        var isWireless = string.Equals(accessPoint.Interface.Type, "WLAN", StringComparison.OrdinalIgnoreCase);
        positions.TryAdd(clientMacAddress, new ClientPosition(
            accessPointMacAddress,
            StringValues.NullIfBlank(accessPoint.Node.Name),
            isWireless ? GetBand(accessPoint.Interface.Name) : null,
            isWireless ? null : GetPort(accessPoint.Interface.Name),
            GetSpeed(link),
            isWireless));
    }

    private static List<AccessPointInfo> BuildAccessPoints(
        List<MeshNode> nodes,
        Dictionary<MeshNode, HashSet<string>> nodeMacAddresses,
        Dictionary<MeshNode, MeshNodeLink> uplinks)
    {
        var accessPoints = new List<AccessPointInfo>();
        foreach (var node in nodes)
        {
            if (!IsMeshNode(node) || MacAddresses.Normalize(node.MacAddress) is not { } macAddress)
            {
                continue;
            }

            var isMaster = IsMaster(node);
            uplinks.TryGetValue(node, out var uplink);
            accessPoints.Add(new AccessPointInfo(
                macAddress,
                nodeMacAddresses[node],
                StringValues.NullIfBlank(node.Name),
                StringValues.NullIfBlank(node.Model),
                StringValues.NullIfBlank(node.Manufacturer),
                StringValues.NullIfBlank(node.FirmwareVersion),
                isMaster,
                isMaster || uplink is not null,
                StringValues.NullIfBlank(uplink?.Type),
                uplink is null ? null : GetSpeed(uplink)));
        }

        return accessPoints;
    }

    /// <returns>The band named in an interface name such as <c>AP:5G:0</c>, or null.</returns>
    internal static string? GetBand(string? interfaceName)
    {
        if (interfaceName is null)
        {
            return null;
        }

        if (interfaceName.Contains("2G", StringComparison.Ordinal))
        {
            return FritzBoxWlanRoles.Band24;
        }

        if (interfaceName.Contains("5G", StringComparison.Ordinal))
        {
            return FritzBoxWlanRoles.Band5;
        }

        return interfaceName.Contains("6G", StringComparison.Ordinal) ? FritzBoxWlanRoles.Band6 : null;
    }

    /// <returns>The port number at the end of an interface name such as <c>LAN:1</c>, or null.</returns>
    internal static int? GetPort(string? interfaceName)
    {
        if (interfaceName is null)
        {
            return null;
        }

        var separator = interfaceName.LastIndexOf(':');
        return separator >= 0 &&
               int.TryParse(interfaceName.AsSpan(separator + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
            ? port
            : null;
    }

    /// <returns>Whether the node is part of the mesh: the router always is, even when it reports no flag.</returns>
    private static bool IsMeshNode(MeshNode node) => node.IsMeshed == true || IsMaster(node);

    private static bool IsMaster(MeshNode node) =>
        string.Equals(node.MeshRole, "master", StringComparison.OrdinalIgnoreCase);

    private static decimal? GetSpeed(MeshNodeLink link)
    {
        var kilobitsPerSecond = Math.Max(link.CurrentDataRateReceive ?? 0, link.CurrentDataRateTransmit ?? 0);
        return kilobitsPerSecond > 0 ? kilobitsPerSecond / 1000m : null;
    }

    private static void AddAddress(HashSet<string> addresses, string? macAddress)
    {
        if (MacAddresses.Normalize(macAddress) is { } normalized)
        {
            addresses.Add(normalized);
        }
    }
}
