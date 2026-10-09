using System.Net;
using System.Net.Sockets;
using Rssdp;

namespace Namotion.Devices.Sonos.Client;

internal static class SonosDiscovery
{
    private const string ZonePlayerSearchTarget = "urn:schemas-upnp-org:device:ZonePlayer:1";
    private static readonly TimeSpan SearchTime = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Builds a speaker URI from <c>host</c> or <c>host:port</c>; the port defaults to the Sonos port 1400.
    /// </summary>
    internal static Uri CreateDeviceUri(string host)
    {
        var uri = new Uri("http://" + host.Trim());
        return new UriBuilder("http", uri.Host, uri.IsDefaultPort ? SonosValues.DevicePort : uri.Port).Uri;
    }

    /// <summary>
    /// Returns the local address the OS would use to reach the host, or null when no route exists.
    /// </summary>
    internal static string? DetectLocalAddress(string remoteHost)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            // Connecting a UDP socket sends nothing; it only makes the OS choose the route and its local address.
            socket.Connect(remoteHost, SonosValues.DevicePort);
            return (socket.LocalEndPoint as IPEndPoint)?.Address.ToString();
        }
        catch (SocketException)
        {
            return null;
        }
    }

    /// <summary>
    /// Searches all IPv4 interfaces for any Sonos speaker and returns its base URI.
    /// </summary>
    internal static async Task<Uri?> FindSpeakerAsync(CancellationToken cancellationToken)
    {
        using var locator = new AggregateSsdpDeviceLocator(includeIpv4: true, includeIpv6: false, adapterFilter: null, logger: null);
        var devices = await locator.SearchAsync(ZonePlayerSearchTarget, SearchTime, cancellationToken);
        var location = devices.Select(device => device.DescriptionLocation).FirstOrDefault(uri => uri is not null);
        return location is null ? null : new UriBuilder("http", location.Host, location.Port).Uri;
    }
}
