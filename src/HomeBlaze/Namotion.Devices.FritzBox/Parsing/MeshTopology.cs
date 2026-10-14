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

    internal static MeshTopology Build(MeshList meshList)
    {
        var nodes = meshList.Nodes ?? [];
        var interfaces = new Dictionary<string, (MeshNode Node, MeshNodeInterface Interface)>(StringComparer.Ordinal);
        var links = new Dictionary<string, MeshNodeLink>(StringComparer.Ordinal);
        var meshMacAddresses = new HashSet<string>(StringComparer.Ordinal);
        var nodeMacAddresses = new Dictionary<MeshNode, HashSet<string>>(ReferenceEqualityComparer.Instance);

        foreach (var node in nodes)
        {
            var addresses = CollectNodeAddresses(node, interfaces, links);
            nodeMacAddresses[node] = addresses;
            if (node.IsMeshed)
            {
                meshMacAddresses.UnionWith(addresses);
            }
        }

        var uplinks = new Dictionary<MeshNode, MeshNodeLink>(ReferenceEqualityComparer.Instance);
        var positions = BuildClientPositions(links, interfaces, uplinks);
        var accessPoints = BuildAccessPoints(nodes, nodeMacAddresses, uplinks);

        return new MeshTopology(accessPoints, positions, meshMacAddresses);
    }

    // Keeps the per-node nesting at two levels: one extraction per node, one for its links.
    private static HashSet<string> CollectNodeAddresses(
        MeshNode node,
        Dictionary<string, (MeshNode Node, MeshNodeInterface Interface)> interfaces,
        Dictionary<string, MeshNodeLink> links)
    {
        var addresses = new HashSet<string>(StringComparer.Ordinal);
        AddAddress(addresses, node.MacAddress);
        foreach (var nodeInterface in node.Interfaces ?? [])
        {
            if (nodeInterface.Uid is { } interfaceUid)
            {
                interfaces.TryAdd(interfaceUid, (node, nodeInterface));
            }

            AddAddress(addresses, nodeInterface.MacAddress);
            CollectLinks(nodeInterface, links);
        }

        return addresses;
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

    private static Dictionary<string, ClientPosition> BuildClientPositions(
        Dictionary<string, MeshNodeLink> links,
        Dictionary<string, (MeshNode Node, MeshNodeInterface Interface)> interfaces,
        Dictionary<MeshNode, MeshNodeLink> uplinks)
    {
        var positions = new Dictionary<string, ClientPosition>(StringComparer.Ordinal);
        foreach (var link in links.Values)
        {
            if (!TryGetConnectedEndpoints(link, interfaces, out var first, out var second))
            {
                continue;
            }

            if (first.Node.IsMeshed && second.Node.IsMeshed)
            {
                // Between two repeaters the direction is unknown, so each takes the first link it has.
                if (!IsMaster(first.Node))
                {
                    uplinks.TryAdd(first.Node, link);
                }

                if (!IsMaster(second.Node))
                {
                    uplinks.TryAdd(second.Node, link);
                }

                continue;
            }

            if (first.Node.IsMeshed == second.Node.IsMeshed)
            {
                continue;
            }

            AddClientPosition(positions, link, first, second);
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
        (MeshNode Node, MeshNodeInterface Interface) second)
    {
        var (accessPoint, client) = first.Node.IsMeshed ? (first, second) : (second, first);
        var clientMacAddress = MacAddresses.Normalize(client.Node.MacAddress) ?? MacAddresses.Normalize(client.Interface.MacAddress);
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
            if (!node.IsMeshed || MacAddresses.Normalize(node.MacAddress) is not { } macAddress)
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
