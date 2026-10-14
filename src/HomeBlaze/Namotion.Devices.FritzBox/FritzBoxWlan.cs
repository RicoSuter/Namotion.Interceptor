using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Devices;
using Namotion.Devices.FritzBox.Model;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.FritzBox;

/// <summary>
/// One WLAN of the router: a radio band, or the guest WLAN.
/// </summary>
[InterceptorSubject]
public partial class FritzBoxWlan : ISwitchState, ITitleProvider
{
    public FritzBoxWlan(string role, string serviceType)
    {
        Role = role;
        ServiceType = serviceType;
        IsOn = null;
        Ssid = null;
        Band = null;
        Channel = null;
        ClientCount = null;
    }

    /// <summary>
    /// The key of this WLAN, see <see cref="FritzBoxWlanRoles"/>.
    /// </summary>
    [State(Position = 400)]
    public partial string Role { get; internal set; }

    /// <summary>
    /// Whether the WLAN is enabled.
    /// </summary>
    public partial bool? IsOn { get; internal set; }

    [State(Position = 401)]
    public partial string? Ssid { get; internal set; }

    [State(Position = 402)]
    public partial string? Band { get; internal set; }

    [State(Position = 403)]
    public partial int? Channel { get; internal set; }

    [State(Position = 404)]
    public partial int? ClientCount { get; internal set; }

    [Derived]
    public string? Title => GetTitle();

    internal string ServiceType { get; private set; }

    private string GetTitle()
    {
        if (Role == FritzBoxWlanRoles.Guest)
        {
            return Ssid is null ? "Guest WLAN" : $"Guest WLAN {Ssid}";
        }

        return Ssid is null ? $"WLAN {Role}" : $"WLAN {Role} {Ssid}";
    }

    internal void Update(WlanInfo info, bool isSwitchStateCurrent)
    {
        ServiceType = info.ServiceType;
        if (isSwitchStateCurrent)
        {
            IsOn = info.IsEnabled;
        }

        Ssid = info.Ssid;
        Band = info.Band;
        Channel = info.Channel;
        ClientCount = info.ClientCount;
    }
}
