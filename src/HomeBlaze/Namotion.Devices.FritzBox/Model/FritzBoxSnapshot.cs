using Namotion.Devices.FritzBox.Parsing;

namespace Namotion.Devices.FritzBox.Model;

/// <summary>
/// Everything one poll read from the router. A part is null when the router did not answer it, except
/// <see cref="Wlans"/> and <see cref="Hosts"/>, which are empty rather than null when unanswered.
/// </summary>
internal sealed record FritzBoxSnapshot(
    RouterInfo? Router,
    string? AvailableSoftwareUpdate,
    InternetInfo? Internet,
    TrafficInfo? Traffic,
    IReadOnlyList<WlanInfo> Wlans,
    IReadOnlyList<HostEntry> Hosts,
    MeshTopology? Mesh);

internal sealed record RouterInfo(
    string? Model,
    string? SerialNumber,
    string? HardwareRevision,
    string? SoftwareVersion,
    TimeSpan? Uptime);

/// <summary>
/// The state of the internet connection. <c>MaximumUploadRate</c> and <c>MaximumDownloadRate</c> are in Mbit/s.
/// </summary>
internal sealed record InternetInfo(
    bool? IsConnected,
    string? ExternalIpAddress,
    string? ExternalIpv6Address,
    TimeSpan? Uptime,
    decimal? MaximumUploadRate,
    decimal? MaximumDownloadRate);

/// <summary>
/// The current traffic on the internet connection. <c>UploadRate</c> and <c>DownloadRate</c> are in kB/s.
/// </summary>
internal sealed record TrafficInfo(decimal? UploadRate, decimal? DownloadRate);

/// <summary>
/// One WLAN of the router. <c>Role</c> is the key in <c>FritzBox.Wlans</c>. <c>ServiceType</c> is the
/// <c>WLANConfiguration</c> service of this WLAN.
/// </summary>
internal sealed record WlanInfo(
    string Role,
    string ServiceType,
    bool? IsEnabled,
    string? Ssid,
    string? Band,
    int? Channel,
    int? ClientCount);
