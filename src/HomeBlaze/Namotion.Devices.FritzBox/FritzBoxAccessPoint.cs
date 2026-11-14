using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using HomeBlaze.Abstractions.Devices;
using HomeBlaze.Abstractions.Networking;
using Namotion.Devices.FritzBox.Parsing;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.FritzBox;

/// <summary>
/// A meshed access point: the router itself, a FRITZ!Repeater or another FRITZ!Box in the mesh, with the clients
/// connected to it.
/// </summary>
[InterceptorSubject]
public partial class FritzBoxAccessPoint : IDeviceInfo, ISoftwareState, INetworkAdapter, IConnectionState, ITitleProvider, IIconProvider
{
    public FritzBoxAccessPoint(string macAddress)
    {
        MacAddress = macAddress;
        Name = null;
        Manufacturer = null;
        Model = null;
        SoftwareVersion = null;
        IpAddress = null;
        IsWireless = null;
        IsConnected = false;
        MeshRole = FritzBoxMeshRole.Unknown;
        UplinkType = null;
        UplinkSpeed = null;
        Clients = new Dictionary<string, FritzBoxClient>(StringComparer.Ordinal);
    }

    [State(Position = 400)]
    public partial string? Name { get; internal set; }

    public partial string? Manufacturer { get; internal set; }

    public partial string? Model { get; internal set; }

    public string? ProductCode => null;

    public string? SerialNumber => null;

    public string? HardwareRevision => null;

    public partial string? SoftwareVersion { get; internal set; }

    public string? AvailableSoftwareUpdate => null;

    public partial string? IpAddress { get; internal set; }

    public partial string MacAddress { get; internal set; }

    public string? SubnetMask => null;

    public string? Gateway => null;

    /// <summary>
    /// Whether the uplink towards the router is wireless; false for the router itself.
    /// </summary>
    public partial bool? IsWireless { get; internal set; }

    public int? SignalStrength => null;

    /// <summary>
    /// Whether the access point is part of the mesh right now.
    /// </summary>
    public partial bool IsConnected { get; internal set; }

    [State(Position = 401)]
    [Category("Mesh")]
    public partial FritzBoxMeshRole MeshRole { get; internal set; }

    [State(Position = 402)]
    [Category("Mesh")]
    public partial string? UplinkType { get; internal set; }

    [State(Position = 403, Unit = StateUnit.MegabitPerSecond)]
    [Category("Mesh")]
    public partial decimal? UplinkSpeed { get; internal set; }

    /// <summary>
    /// The present clients connected to this access point, the same subjects as in <see cref="FritzBox.Clients"/>.
    /// </summary>
    [State]
    public partial Dictionary<string, FritzBoxClient> Clients { get; internal set; }

    [Derived]
    [State(Position = 404)]
    public int ClientCount => Clients.Count;

    [Derived]
    public string? Title => Name ?? Model ?? MacAddress;

    [Derived]
    public string IconName => "Router";

    [Derived]
    public string? IconColor => IsConnected ? "Success" : null;

    internal void Update(AccessPointInfo info, string? ipAddress)
    {
        Name = info.Name;
        Manufacturer = info.Manufacturer;
        Model = info.Model;
        SoftwareVersion = info.SoftwareVersion;
        IpAddress = ipAddress;
        IsConnected = info.IsConnected;
        MeshRole = info.IsMaster ? FritzBoxMeshRole.Master : FritzBoxMeshRole.Slave;
        UplinkType = info.UplinkType;
        UplinkSpeed = info.UplinkSpeed;
        IsWireless = GetIsWireless(info);
    }

    private static bool? GetIsWireless(AccessPointInfo info)
    {
        if (info.IsMaster)
        {
            return false;
        }

        return info.UplinkType is null ? null : string.Equals(info.UplinkType, "WLAN", StringComparison.OrdinalIgnoreCase);
    }
}
