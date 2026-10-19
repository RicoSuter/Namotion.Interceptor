namespace Namotion.Devices.FritzBox.Parsing;

/// <summary>
/// One item of the FRITZ!Box host list, as the router reports it. <c>Speed</c> is the link speed in Mbit/s.
/// </summary>
internal sealed record HostEntry(
    string? MacAddress,
    string? IpAddress,
    bool IsActive,
    string? HostName,
    string? InterfaceType,
    int? Port,
    int? Speed,
    bool IsGuest);
