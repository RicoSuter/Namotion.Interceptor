namespace Namotion.Devices.Sonos.Parsing;

internal sealed record SonosTopology(IReadOnlyList<SonosTopologyGroup> Groups);

internal sealed record SonosTopologyGroup(
    string Id,
    string CoordinatorUuid,
    IReadOnlyList<SonosTopologyPlayer> Players);

internal sealed record SonosTopologyPlayer(
    string Uuid,
    string RoomName,
    Uri BaseUri,
    string? SoftwareVersion,
    bool? IsWireless,
    string? MoreInfo,
    IReadOnlyList<SonosTopologySatellite> Satellites);

internal sealed record SonosTopologySatellite(
    string Uuid,
    string RoomName,
    Uri BaseUri,
    string? SoftwareVersion,
    bool? IsWireless,
    SonosSatelliteRole Role);
