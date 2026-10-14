using HomeBlaze.Abstractions.Networking;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.FritzBox.Tests.Testing;

/// <summary>
/// A device subject of another library, which reports its MAC and IP address and is configured with a host address.
/// </summary>
[InterceptorSubject]
public partial class TestNetworkDevice : INetworkAdapter, INetworkHost
{
    public TestNetworkDevice(string? macAddress = null, string? ipAddress = null, string? hostAddress = null)
    {
        MacAddress = macAddress;
        IpAddress = ipAddress;
        HostAddress = hostAddress;
    }

    public partial string? MacAddress { get; set; }

    public partial string? IpAddress { get; set; }

    public partial string? HostAddress { get; set; }

    public string? SubnetMask => null;

    public string? Gateway => null;

    public bool? IsWireless => null;

    public int? SignalStrength => null;
}
