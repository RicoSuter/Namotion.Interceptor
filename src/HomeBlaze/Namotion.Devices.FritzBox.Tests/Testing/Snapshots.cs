using Namotion.Devices.FritzBox.Model;
using Namotion.Devices.FritzBox.Parsing;

namespace Namotion.Devices.FritzBox.Tests.Testing;

internal static class Snapshots
{
    internal const string Laptop = "AA:AA:AA:00:00:01";
    internal const string Desktop = "AA:AA:AA:00:00:02";
    internal const string Phone = "AA:AA:AA:00:00:03";
    internal const string RouterAccessPoint = "BB:BB:BB:00:00:01";
    internal const string RepeaterAccessPoint = "BB:BB:BB:00:00:02";

    internal static List<HostEntry> Hosts()
    {
        using var stream = Fixtures.Open("hostlist.xml");
        return HostListParser.Parse(stream);
    }

    internal static MeshTopology Mesh()
    {
        using var stream = Fixtures.Open("meshlist.json");
        return MeshTopology.Build(MeshListParser.Parse(stream));
    }

    internal static IReadOnlyList<WlanInfo> Wlans(bool isGuestEnabled = false) =>
    [
        new(FritzBoxWlanRoles.Band24, "urn:dslforum-org:service:WLANConfiguration:1", true, "Home", FritzBoxWlanRoles.Band24, 6, 3),
        new(FritzBoxWlanRoles.Band5, "urn:dslforum-org:service:WLANConfiguration:2", true, "Home", FritzBoxWlanRoles.Band5, 36, 5),
        new(FritzBoxWlanRoles.Guest, "urn:dslforum-org:service:WLANConfiguration:3", isGuestEnabled, "Guests", FritzBoxWlanRoles.Band24, 6, 0)
    ];

    internal static RouterInfo Router(string serialNumber = "SERIAL0001") =>
        new("FRITZ!Box 7590", serialNumber, "FRITZ!Box 7590", "154.07.57", TimeSpan.FromHours(1));

    internal static FritzBoxSnapshot Create(
        IReadOnlyList<HostEntry>? hosts = null,
        MeshTopology? mesh = null,
        bool hasMesh = true,
        IReadOnlyList<WlanInfo>? wlans = null,
        RouterInfo? router = null,
        TrafficInfo? traffic = null) =>
        new(
            router ?? Router(),
            null,
            new InternetInfo(true, "203.0.113.7", "2001:db8::7", TimeSpan.FromHours(2), 40m, 250m),
            traffic ?? new TrafficInfo(5m, 120m),
            wlans ?? Wlans(),
            hosts ?? Hosts(),
            hasMesh ? mesh ?? Mesh() : null);
}
