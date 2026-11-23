using System.Net;
using System.Net.Sockets;
using System.Xml;
using HomeBlaze.Abstractions.Discovery;
using Namotion.Devices.FritzBox.Client;

namespace Namotion.Devices.FritzBox;

/// <summary>
/// A FRITZ!Box found on the local network.
/// </summary>
/// <param name="HostAddress">The host name or IP address the router answered on.</param>
/// <param name="FriendlyName">The friendly name from its TR-064 description, or null when it has none.</param>
/// <param name="Model">The model name from its TR-064 description, or null when it has none.</param>
/// <param name="Udn">The unique device name from its TR-064 description, or null when it has none.</param>
/// <param name="IsRepeater">
/// Whether the device offers no internet connection, as a FRITZ!Repeater: in a FRITZ!Box mesh its clients already
/// appear under the router.
/// </param>
public sealed record FritzBoxDiscoveryResult(string HostAddress, string? FriendlyName, string? Model, string? Udn, bool IsRepeater)
    : IDiscoveryResult
{
    /// <inheritdoc />
    public string Key => Udn ?? HostAddress;

    /// <inheritdoc />
    public string DisplayName => FriendlyName ?? Model ?? FritzBox.DefaultName;

    /// <inheritdoc />
    public string? Detail => IsRepeater ? $"{HostAddress} (repeater)" : HostAddress;
}

/// <summary>
/// Finds FRITZ!Box routers on the local network over SSDP and by their usual host name.
/// </summary>
public static class FritzBoxDiscovery
{
    private const string SearchTarget = "urn:dslforum-org:device:InternetGatewayDevice:1";
    private const string WellKnownHost = "fritz.box";

    /// <summary>
    /// The most locations described in one search, so that a hostile or malfunctioning answerer on the network
    /// cannot make a search describe an unbounded number of hosts.
    /// </summary>
    private const int MaximumLocations = 32;

    /// <summary>
    /// Searches with <paramref name="ssdpSearch"/> and asks <c>fritz.box</c> when that name resolves to an address on
    /// the local network, then describes each router found in parallel. Never throws for a location that does not
    /// answer or does not describe a FRITZ!Box: it is left out of the result. Cancellation still propagates.
    /// </summary>
    /// <param name="ssdpSearch">The SSDP search to use.</param>
    /// <param name="timeout">How long to wait for each description request.</param>
    /// <param name="cancellationToken">Cancels the search and every description request.</param>
    /// <returns>
    /// The routers found, de-duplicated by unique device name, or by host address when that is absent, with
    /// repeaters last. A router found by both SSDP and the <c>fritz.box</c> fallback is kept once, under its SSDP
    /// host address.
    /// </returns>
    public static Task<IReadOnlyList<FritzBoxDiscoveryResult>> DiscoverAsync(
        ISsdpSearch ssdpSearch, TimeSpan timeout, CancellationToken cancellationToken) =>
        DiscoverAsync(ssdpSearch, timeout, Dns.GetHostAddressesAsync, cancellationToken);

    internal static async Task<IReadOnlyList<FritzBoxDiscoveryResult>> DiscoverAsync(
        ISsdpSearch ssdpSearch,
        TimeSpan timeout,
        Func<string, CancellationToken, Task<IPAddress[]>> resolveAsync,
        CancellationToken cancellationToken)
    {
        // Without a FRITZ!Box on the network the name resolves to AVM's public server or not at all, and a LAN
        // discovery must not send requests to the internet.
        var isWellKnownHostLocal = IsOnLocalNetworkAsync(WellKnownHost, resolveAsync, timeout, cancellationToken);
        var responses = await ssdpSearch.SearchAsync(SearchTarget, cancellationToken);
        var hosts = responses
            .Select(response => response.Location.Host)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaximumLocations - 1);
        if (await isWellKnownHostLocal)
        {
            hosts = hosts.Append(WellKnownHost);
        }

        using var httpClient = FritzBoxHttpClient.CreatePlain(timeout, Tr64Description.MaximumBytes);

        // A slot is reserved for the fritz.box fallback so a flood of SSDP answers cannot push it out of the cap.
        var distinctHosts = hosts.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var descriptions = await Task.WhenAll(
            distinctHosts.Select(host => DescribeAsync(httpClient, host, Tr64Description.DescriptionPort, cancellationToken)));

        var results = new Dictionary<string, FritzBoxDiscoveryResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var result in descriptions)
        {
            if (result is not null)
            {
                results.TryAdd(result.Key, result);
            }
        }

        return results.Values.OrderBy(result => result.IsRepeater).ToArray();
    }

    /// <summary>
    /// Whether <paramref name="host"/> resolves, within <paramref name="timeout"/>, to at least one address on the
    /// local network. A name that does not resolve is not on it. Cancellation still propagates.
    /// </summary>
    internal static async Task<bool> IsOnLocalNetworkAsync(
        string host, Func<string, CancellationToken, Task<IPAddress[]>> resolveAsync, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            var addresses = await resolveAsync(host, cancellationToken).WaitAsync(timeout, cancellationToken);
            return addresses.Any(IsLocalNetworkAddress);
        }
        catch (Exception exception) when ((exception is SocketException or TimeoutException) && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether an address is a private IPv4 address (RFC 1918), a link-local address, or an IPv6 unique local address.
    /// </summary>
    internal static bool IsLocalNetworkAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal;
        }

        Span<byte> bytes = stackalloc byte[4];
        return address.TryWriteBytes(bytes, out _) && IsLocalNetworkAddress(bytes[0], bytes[1]);
    }

    private static bool IsLocalNetworkAddress(byte first, byte second) => first switch
    {
        10 => true,
        172 => second is >= 16 and <= 31,
        192 => second == 168,
        169 => second == 254,
        _ => false
    };

    /// <summary>
    /// Describes the router at a host, ignoring whatever scheme, port or path an SSDP answer advertised: the
    /// description is always fetched the way an opened connection fetches it, plain HTTP on <paramref name="port"/>
    /// at the fixed TR-064 path.
    /// </summary>
    /// <returns>
    /// The router at the host, or null when it does not answer, answers with something unparseable or oversized, or
    /// lists no <c>DeviceInfo</c> service.
    /// </returns>
    internal static async Task<FritzBoxDiscoveryResult?> DescribeAsync(HttpClient httpClient, string host, int port, CancellationToken cancellationToken)
    {
        try
        {
            var description = await Tr64Description.LoadAsync(httpClient, host, port, cancellationToken);
            if (description.TryGetService(Tr64Services.DeviceInfo) is null)
            {
                return null;
            }

            return new FritzBoxDiscoveryResult(
                host, description.FriendlyName, description.ModelName, description.Udn,
                IsRepeater: !description.HasService(Tr64Services.WanCommonInterfaceConfig));
        }
        catch (Exception exception) when ((exception is HttpRequestException or XmlException or TaskCanceledException) && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}
