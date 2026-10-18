using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Namotion.Devices.Nuki;

/// <summary>
/// A bridge that the Nuki discovery service lists for the public IP address of the caller.
/// </summary>
/// <param name="BridgeId">The ID of the bridge.</param>
/// <param name="IpAddress">The local IP address of the bridge.</param>
/// <param name="Port">The port of the HTTP API.</param>
public sealed record NukiDiscoveredBridge(long BridgeId, string IpAddress, int Port)
{
    /// <summary>The address to configure as <see cref="NukiBridge.HostAddress"/>, with an IPv6 address in brackets.</summary>
    public string HostAddress =>
        (IPAddress.TryParse(IpAddress, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6
            ? "[" + IpAddress + "]"
            : IpAddress) + ":" + Port.ToString(CultureInfo.InvariantCulture);
}
