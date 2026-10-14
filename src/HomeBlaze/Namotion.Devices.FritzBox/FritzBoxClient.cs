using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Networking;
using HomeBlaze.Abstractions.Sensors;
using Namotion.Devices.FritzBox.Parsing;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.FritzBox;

/// <summary>
/// A device in the FRITZ!Box host list, such as a computer or a phone, keyed by its MAC address.
/// </summary>
[InterceptorSubject]
public partial class FritzBoxClient : IPresenceSensor, INetworkAdapter, ITitleProvider, IIconProvider
{
    public FritzBoxClient(string macAddress)
    {
        MacAddress = macAddress;
        HostName = null;
        IsPresent = null;
        IpAddress = null;
        IsWireless = null;
        InterfaceType = FritzBoxInterfaceType.Unknown;
        IsGuest = null;
        AccessPointName = null;
        AccessPointMacAddress = null;
        Band = null;
        Port = null;
        LinkSpeed = null;
    }

    [State(Position = 400)]
    public partial string? HostName { get; internal set; }

    /// <summary>
    /// Whether the router currently sees the device as active.
    /// </summary>
    public partial bool? IsPresent { get; internal set; }

    public partial string? IpAddress { get; internal set; }

    public partial string MacAddress { get; internal set; }

    public string? SubnetMask => null;

    public string? Gateway => null;

    public partial bool? IsWireless { get; internal set; }

    public int? SignalStrength => null;

    [State(Position = 401)]
    public partial FritzBoxInterfaceType InterfaceType { get; internal set; }

    [State(Position = 402, IsDiscrete = true)]
    public partial bool? IsGuest { get; internal set; }

    /// <summary>
    /// The name of the access point the device is connected to, null while it is absent.
    /// </summary>
    [State(Position = 230)]
    public partial string? AccessPointName { get; internal set; }

    [State(Position = 231)]
    public partial string? AccessPointMacAddress { get; internal set; }

    /// <summary>
    /// The WLAN band, null for a wired or absent device.
    /// </summary>
    [State(Position = 232)]
    public partial string? Band { get; internal set; }

    /// <summary>
    /// The LAN port of the access point, null for a wireless or absent device.
    /// </summary>
    [State(Position = 233)]
    public partial int? Port { get; internal set; }

    [State(Position = 234, Unit = StateUnit.MegabitPerSecond)]
    public partial decimal? LinkSpeed { get; internal set; }

    [Derived]
    public string? Title => HostName ?? IpAddress ?? MacAddress;

    [Derived]
    public string IconName => IsWireless == true ? "Wifi" : "SettingsEthernet";

    [Derived]
    public string? IconColor => IsPresent == true ? "Success" : null;

    internal void Update(HostEntry host, ClientPosition? position)
    {
        HostName = StringValues.NullIfBlank(host.HostName);
        IpAddress = StringValues.NullIfBlank(host.IpAddress);
        IsPresent = host.IsActive;
        InterfaceType = FritzBoxInterfaceTypes.Parse(host.InterfaceType);
        IsWireless = GetIsWireless(InterfaceType, position);
        IsGuest = host.IsGuest;
        AccessPointName = position?.AccessPointName;
        AccessPointMacAddress = position?.AccessPointMacAddress;
        Band = position?.Band;
        Port = position?.Port;
        LinkSpeed = GetLinkSpeed(host, position);
    }

    private static bool? GetIsWireless(FritzBoxInterfaceType interfaceType, ClientPosition? position)
    {
        if (position is not null)
        {
            return position.IsWireless;
        }

        return interfaceType switch
        {
            FritzBoxInterfaceType.Wlan => true,
            FritzBoxInterfaceType.Ethernet or FritzBoxInterfaceType.HomePlug => false,
            _ => null
        };
    }

    private static decimal? GetLinkSpeed(HostEntry host, ClientPosition? position)
    {
        if (position is not null)
        {
            return position.LinkSpeed;
        }

        return host is { IsActive: true, Speed: > 0 } ? host.Speed : null;
    }
}
