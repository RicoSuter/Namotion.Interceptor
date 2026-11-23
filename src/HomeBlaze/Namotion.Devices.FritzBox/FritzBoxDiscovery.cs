using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
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
/// Finds FRITZ!Box routers and repeaters on the local network over SSDP and by their usual host name,
/// <c>fritz.box</c>.
/// </summary>
public sealed class FritzBoxDiscovery : SubjectDiscovery<FritzBox, FritzBoxDiscoveryResult>
{
    private const string SearchTarget = "urn:dslforum-org:device:InternetGatewayDevice:1";
    private const string WellKnownHost = "fritz.box";

    private static readonly TimeSpan DescriptionTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The most locations described in one search, so that a hostile or malfunctioning answerer on the network
    /// cannot make a search describe an unbounded number of hosts.
    /// </summary>
    private const int MaximumLocations = 32;

    /// <inheritdoc />
    protected override async IAsyncEnumerable<FritzBoxDiscoveryResult> DiscoverAsync(
        DiscoveryScan scan, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var result in await SearchAsync(scan.Ssdp, DescriptionTimeout, cancellationToken))
        {
            yield return result;
        }
    }

    /// <inheritdoc />
    protected override bool Matches(FritzBox existing, FritzBoxDiscoveryResult result) => IsSameRouter(existing, result);

    // The UDN recognizes a router that moved to another address; the host address one that was added without
    // discovery, and a router that replaced it at the same address, which a stale stored UDN must not hide.
    internal static bool IsSameRouter(FritzBox existing, FritzBoxDiscoveryResult result) =>
        (result.Udn is not null && string.Equals(existing.Udn?.Trim(), result.Udn, StringComparison.OrdinalIgnoreCase)) ||
        string.Equals(existing.HostAddress?.Trim(), result.HostAddress, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Searches with <paramref name="ssdpSearch"/> and asks <c>fritz.box</c> when that name resolves to an address on
    /// the local network, then describes each router found in parallel. Never throws for a location that does not
    /// answer or does not describe a FRITZ!Box: it is left out of the result. Cancellation still propagates.
    /// </summary>
    /// <param name="ssdpSearch">The SSDP search to use.</param>
    /// <param name="timeout">
    /// How long to wait for the name resolution of <c>fritz.box</c> and for each description request.
    /// </param>
    /// <param name="cancellationToken">Cancels the search and every description request.</param>
    /// <returns>
    /// The routers found, de-duplicated by unique device name, or by host address when that is absent, with
    /// repeaters last. A router found by both SSDP and the <c>fritz.box</c> fallback is kept once, under its SSDP
    /// host address.
    /// </returns>
    public static async Task<IReadOnlyList<FritzBoxDiscoveryResult>> SearchAsync(
        ISsdpSearch ssdpSearch, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var httpClient = FritzBoxHttpClient.CreatePlain(timeout, Tr64Description.MaximumBytes);
        return await SearchAsync(
            ssdpSearch,
            timeout,
            Dns.GetHostAddressesAsync,
            (host, token) => DescribeAsync(httpClient, host, Tr64Description.DescriptionPort, token),
            TimeProvider.System,
            cancellationToken);
    }

    internal static async Task<IReadOnlyList<FritzBoxDiscoveryResult>> SearchAsync(
        ISsdpSearch ssdpSearch,
        TimeSpan timeout,
        Func<string, CancellationToken, Task<IPAddress[]>> resolveAsync,
        Func<string, CancellationToken, Task<FritzBoxDiscoveryResult?>> describeAsync,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        // Without a FRITZ!Box on the network the name resolves to AVM's public server or not at all, and the fallback
        // must not send a request to the internet.
        var isWellKnownHostLocal = IsOnLocalNetworkAsync(WellKnownHost, resolveAsync, timeout, timeProvider, cancellationToken);
        var responses = await ssdpSearch.SearchAsync(SearchTarget, cancellationToken);

        // A slot is reserved for the fritz.box fallback so a flood of SSDP answers cannot push it out of the cap.
        var hosts = responses
            .Select(response => response.Location.Host)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaximumLocations - 1);
        if (await isWellKnownHostLocal)
        {
            hosts = hosts.Append(WellKnownHost);
        }

        var distinctHosts = hosts.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var descriptions = await Task.WhenAll(distinctHosts.Select(host => describeAsync(host, cancellationToken)));

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
        string host,
        Func<string, CancellationToken, Task<IPAddress[]>> resolveAsync,
        TimeSpan timeout,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = new CancellationTokenSource(timeout, timeProvider);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            // On Linux a running lookup ignores its token, so the wait itself must end at the timeout.
            var addresses = await resolveAsync(host, linkedSource.Token).WaitAsync(linkedSource.Token);
            return addresses.Any(IsLocalNetworkAddress);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (SocketException) when (!cancellationToken.IsCancellationRequested)
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
