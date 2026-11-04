using HomeBlaze.Abstractions.Networking;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.FritzBox.Tests.Testing;

/// <summary>
/// A device subject of another library, which reports its MAC and IP address, is configured with a host address and
/// is connected unless set otherwise.
/// </summary>
[InterceptorSubject]
public partial class TestNetworkDevice : INetworkAdapter, INetworkHost, IConnectionState
{
    public TestNetworkDevice(string? macAddress = null, string? ipAddress = null, string? hostAddress = null)
    {
        MacAddress = macAddress;
        IpAddress = ipAddress;
        HostAddress = hostAddress;
        IsConnected = true;
    }

    public partial bool IsConnected { get; set; }

    public partial string? MacAddress { get; set; }

    public partial string? IpAddress { get; set; }

    public partial string? HostAddress { get; set; }

    public string? SubnetMask => null;

    public string? Gateway => null;

    public bool? IsWireless => null;

    public int? SignalStrength => null;
}
