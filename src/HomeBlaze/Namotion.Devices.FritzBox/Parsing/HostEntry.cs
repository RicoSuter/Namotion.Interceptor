namespace Namotion.Devices.FritzBox.Parsing;

/// <summary>
/// One item of the FRITZ!Box host list, as the router reports it. <c>Speed</c> is the link speed in Mbit/s.
/// <c>HasWanAccess</c> is whether the router lets the host reach the internet, null when it reports neither granted nor
/// denied. <c>IsWanAccessDisallowed</c> is whether the internet access of the host is blocked on its own, regardless of
/// its filter profile. <c>FilterProfileId</c> is the ID of its filter profile (access profile). Older firmware omits
/// these three.
/// </summary>
internal sealed record HostEntry(
    string? MacAddress,
    string? IpAddress,
    bool IsActive,
    string? HostName,
    string? InterfaceType,
    int? Port,
    int? Speed,
    bool IsGuest,
    bool? HasWanAccess = null,
    bool? IsWanAccessDisallowed = null,
    string? FilterProfileId = null);
