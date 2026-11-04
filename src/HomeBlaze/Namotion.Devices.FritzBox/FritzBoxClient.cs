using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Networking;
using HomeBlaze.Abstractions.Sensors;
using Namotion.Devices.FritzBox.Parsing;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.FritzBox;

/// <summary>
/// A device in the FRITZ!Box host list, such as a computer or a phone, keyed by its MAC address.
/// </summary>
[InterceptorSubject]
public partial class FritzBoxClient : IPresenceSensor, INetworkAdapter, ITitleProvider, IIconProvider
{
    private readonly bool _isPrivateMacAddress;

    public FritzBoxClient(string macAddress)
    {
        MacAddress = macAddress;
        _isPrivateMacAddress = MacAddresses.IsLocallyAdministered(macAddress);
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
        DevicePath = null;
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
    [Category("Network")]
    public partial FritzBoxInterfaceType InterfaceType { get; internal set; }

    [State(Position = 402)]
    [Category("Network")]
    public partial bool? IsGuest { get; internal set; }

    /// <summary>
    /// The name of the access point the device is connected to, null while the device is absent or the mesh list
    /// does not place it.
    /// </summary>
    [State(Position = 403)]
    [Category("Network")]
    public partial string? AccessPointName { get; internal set; }

    [State(Position = 404)]
    [Category("Network")]
    public partial string? AccessPointMacAddress { get; internal set; }

    /// <summary>
    /// The WLAN band, null for a wired device, or while the device is absent or the mesh list does not place it.
    /// </summary>
    [State(Position = 405)]
    [Category("Network")]
    public partial string? Band { get; internal set; }

    /// <summary>
    /// The LAN port of the access point. Taken from the mesh list position when the device is placed there,
    /// otherwise falls back to the host list entry's port while the device is active; null for a wireless
    /// device or when neither source reports one.
    /// </summary>
    [State(Position = 406)]
    [Category("Network")]
    public partial int? Port { get; internal set; }

    [State(Position = 407, Unit = StateUnit.MegabitPerSecond)]
    [Category("Network")]
    public partial decimal? LinkSpeed { get; internal set; }

    /// <summary>
    /// The canonical path of the subject of this device, null when no subject matches it unambiguously, see
    /// <see cref="FritzBoxDeviceMatcher"/>.
    /// </summary>
    [State("Device", Position = 408)]
    [Category("Network")]
    [SubjectPath(typeof(IInterceptorSubject))]
    public partial string? DevicePath { get; internal set; }

    /// <summary>
    /// Whether <see cref="MacAddress"/> is locally administered: a private address, which a phone or computer may
    /// replace with a new one, or the address of a virtual adapter.
    /// </summary>
    [Derived]
    [State(Position = 409)]
    [Category("Network")]
    public bool IsPrivateMacAddress => _isPrivateMacAddress;

    /// <summary>
    /// Whether the router no longer lists the device, see <see cref="Depart"/>.
    /// </summary>
    internal bool IsDeparted { get; private set; }

    [Derived]
    public string? Title => HostName ?? IpAddress ?? MacAddress;

    [Derived]
    public string IconName => IsWireless == true ? "Wifi" : "SettingsEthernet";

    [Derived]
    public string? IconColor => IsPresent == true ? "Success" : null;

    internal void Update(HostEntry host, ClientPosition? position)
    {
        var interfaceType = FritzBoxInterfaceTypes.Parse(host.InterfaceType);

        IsDeparted = false;

        HostName = StringValues.NullIfBlank(host.HostName);
        IpAddress = StringValues.NullIfBlank(host.IpAddress);
        IsPresent = host.IsActive;
        InterfaceType = interfaceType;
        IsWireless = GetIsWireless(interfaceType, position);
        IsGuest = host.IsGuest;
        AccessPointName = position?.AccessPointName;
        AccessPointMacAddress = position?.AccessPointMacAddress;
        Band = position?.Band;
        Port = GetPort(host, position);
        LinkSpeed = GetLinkSpeed(host, position);
    }

    /// <summary>
    /// Marks the device as no longer listed by the router. It becomes absent like a listed absent device and keeps its
    /// last host name and IP address.
    /// </summary>
    internal void Depart()
    {
        IsDeparted = true;
        IsPresent = false;
        AccessPointName = null;
        AccessPointMacAddress = null;
        Band = null;
        Port = null;
        LinkSpeed = null;
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

    private static int? GetPort(HostEntry host, ClientPosition? position)
    {
        if (position is not null)
        {
            return position.Port;
        }

        return host is { IsActive: true, Port: > 0 } ? host.Port : null;
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
