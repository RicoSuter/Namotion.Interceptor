using System.Net.Sockets;
using System.Xml;
using Namotion.Devices.FritzBox.Client;
using Rssdp;

namespace Namotion.Devices.FritzBox;

/// <summary>
/// A FRITZ!Box found on the local network.
/// </summary>
/// <param name="HostAddress">The host name or IP address the router answered on.</param>
/// <param name="FriendlyName">The friendly name from its TR-064 description, or null when it has none.</param>
/// <param name="Model">The model name from its TR-064 description, or null when it has none.</param>
/// <param name="Udn">The unique device name from its TR-064 description, or null when it has none.</param>
public sealed record FritzBoxDiscoveryResult(string HostAddress, string? FriendlyName, string? Model, string? Udn);

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
    /// Searches every IPv4 adapter and asks <c>fritz.box</c>, then describes each router found in parallel. Never
    /// throws for a location that does not answer or does not describe a FRITZ!Box: it is left out of the result.
    /// Cancellation still propagates.
    /// </summary>
    /// <remarks>
    /// A container on a bridge network usually receives no multicast; enter the address by hand in that case instead
    /// of relying on this search.
    /// </remarks>
    /// <param name="timeout">How long to wait for SSDP answers and for each description request.</param>
    /// <param name="cancellationToken">Cancels the search and every description request.</param>
    /// <returns>
    /// The routers found, de-duplicated by unique device name, or by host address when that is absent. A router
    /// found by both SSDP and the <c>fritz.box</c> fallback is kept once, under its SSDP host address.
    /// </returns>
    public static async Task<IReadOnlyList<FritzBoxDiscoveryResult>> DiscoverAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var hosts = new List<string>();
        try
        {
            using var locator = new AggregateSsdpDeviceLocator(includeIpv4: true, includeIpv6: false, adapterFilter: null, logger: null);
            var devices = await locator.SearchAsync(SearchTarget, timeout, cancellationToken);
            foreach (var device in devices)
            {
                // Other UPnP devices can answer a search with any notification type; only a genuine answer for the
                // target, with an absolute location to describe, is usable.
                if (device.NotificationType == SearchTarget && device.DescriptionLocation is { IsAbsoluteUri: true } location)
                {
                    hosts.Add(location.Host);
                }
            }
        }
        catch (Exception exception) when (exception is ArgumentException or SocketException or AggregateException)
        {
            // No IPv4 adapter usable for multicast exists, or the search itself failed; fritz.box is still tried.
        }

        hosts.Add(WellKnownHost);

        using var httpClient = FritzBoxHttpClient.CreatePlain(timeout, Tr64Description.MaximumBytes);
        var distinctHosts = hosts.Distinct(StringComparer.OrdinalIgnoreCase).Take(MaximumLocations).ToArray();
        var descriptions = await Task.WhenAll(
            distinctHosts.Select(host => DescribeAsync(httpClient, host, Tr64Description.DescriptionPort, cancellationToken)));

        var results = new Dictionary<string, FritzBoxDiscoveryResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var result in descriptions)
        {
            if (result is not null)
            {
                results.TryAdd(result.Udn ?? result.HostAddress, result);
            }
        }

        return results.Values.ToArray();
    }

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
            var location = new UriBuilder(Uri.UriSchemeHttp, host, port, Tr64Description.DescriptionPath).Uri;
            var description = Tr64Description.Parse(await httpClient.GetStringAsync(location, cancellationToken));
            if (description.TryGetService(Tr64Services.DeviceInfo) is null)
            {
                return null;
            }

            return new FritzBoxDiscoveryResult(host, description.FriendlyName, description.ModelName, description.Udn);
        }
        catch (Exception exception) when ((exception is HttpRequestException or XmlException or TaskCanceledException) && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}
