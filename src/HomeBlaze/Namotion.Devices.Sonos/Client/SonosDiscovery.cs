using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Rssdp;

namespace Namotion.Devices.Sonos.Client;

internal static class SonosDiscovery
{
    private const string ZonePlayerSearchTarget = "urn:schemas-upnp-org:device:ZonePlayer:1";
    private const string SonosUsnPrefix = "uuid:RINCON_";
    private static readonly TimeSpan SearchTime = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Builds a speaker URI from <c>host</c>, <c>host:port</c>, an IPv6 literal or <c>[IPv6]:port</c>; the port defaults
    /// to the Sonos port 1400.
    /// </summary>
    /// <exception cref="ArgumentException">The input contains a scheme, an invalid host or an invalid port.</exception>
    internal static Uri CreateDeviceUri(string host)
    {
        var value = host.Trim();
        if (value.Contains("://", StringComparison.Ordinal))
        {
            throw new ArgumentException("Expected a host or host:port without a scheme.", nameof(host));
        }

        string hostName;
        string? port = null;
        if (value.StartsWith('['))
        {
            var end = value.IndexOf(']');
            if (end < 0 || (end + 1 < value.Length && value[end + 1] != ':'))
            {
                throw new ArgumentException($"Invalid IPv6 host '{value}'.", nameof(host));
            }

            hostName = value[1..end];
            port = end + 1 < value.Length ? value[(end + 2)..] : null;
        }
        else if (value.Count(character => character == ':') == 1)
        {
            var separator = value.IndexOf(':');
            hostName = value[..separator];
            port = value[(separator + 1)..];
        }
        else
        {
            hostName = value;
        }

        if (Uri.CheckHostName(hostName) == UriHostNameType.Unknown)
        {
            throw new ArgumentException($"Invalid host '{value}'.", nameof(host));
        }

        var portNumber = SonosValues.DevicePort;
        if (port is not null &&
            (!int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out portNumber) || portNumber is < 1 or > 65535))
        {
            throw new ArgumentException($"Invalid port in '{value}'.", nameof(host));
        }

        return new UriBuilder("http", hostName, portNumber).Uri;
    }

    /// <summary>
    /// Returns the local IPv4 address the OS would use to reach the host, or null when the host has no IPv4 address,
    /// cannot be resolved or no route exists.
    /// </summary>
    internal static string? DetectLocalAddress(string remoteHost)
    {
        try
        {
            var host = remoteHost.Trim().Trim('[', ']');
            var remoteAddress = IPAddress.TryParse(host, out var address)
                ? address
                : Dns.GetHostAddresses(host, AddressFamily.InterNetwork).FirstOrDefault();

            if (remoteAddress is not { AddressFamily: AddressFamily.InterNetwork })
            {
                return null;
            }

            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            // Connecting a UDP socket sends nothing; it only makes the OS choose the route and its local address.
            socket.Connect(remoteAddress, SonosValues.DevicePort);
            return (socket.LocalEndPoint as IPEndPoint)?.Address.ToString();
        }
        catch (Exception exception) when (exception is SocketException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Searches all IPv4 interfaces for any Sonos speaker and returns its base URI.
    /// </summary>
    /// <exception cref="ArgumentException">No IPv4 network adapter usable for multicast exists.</exception>
    internal static async Task<Uri?> FindSpeakerAsync(CancellationToken cancellationToken)
    {
        using var locator = new AggregateSsdpDeviceLocator(includeIpv4: true, includeIpv6: false, adapterFilter: null, logger: null);
        var devices = await locator.SearchAsync(ZonePlayerSearchTarget, SearchTime, cancellationToken);
        var location = devices.Where(IsZonePlayer).Select(device => device.DescriptionLocation).FirstOrDefault();
        return location is null ? null : new UriBuilder("http", location.Host, location.Port).Uri;
    }

    /// <summary>
    /// Returns whether a search response is a Sonos zone player with a description location. Other UPnP devices can
    /// answer a search with any target.
    /// </summary>
    internal static bool IsZonePlayer(DiscoveredSsdpDevice device) =>
        device.NotificationType == ZonePlayerSearchTarget &&
        device.Usn?.StartsWith(SonosUsnPrefix, StringComparison.Ordinal) == true &&
        device.DescriptionLocation is not null;
}
