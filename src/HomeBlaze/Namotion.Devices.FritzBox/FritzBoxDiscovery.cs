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

    private static readonly Uri WellKnownLocation =
        new UriBuilder(Uri.UriSchemeHttp, "fritz.box", Tr64Description.DescriptionPort, Tr64Description.DescriptionPath).Uri;

    /// <summary>
    /// Searches every IPv4 adapter and asks <c>fritz.box</c>, then describes each router found. A container on a
    /// bridge network usually receives no multicast; enter the address by hand there. Never throws for a location
    /// that does not answer: it is left out of the result. Cancellation still propagates.
    /// </summary>
    /// <param name="timeout">How long to wait for SSDP answers and for each description request.</param>
    /// <param name="cancellationToken">Cancels the search and every description request.</param>
    /// <returns>The routers found, de-duplicated by unique device name, or by host address when that is absent.</returns>
    public static async Task<IReadOnlyList<FritzBoxDiscoveryResult>> DiscoverAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var locations = new List<Uri>();
        try
        {
            using var locator = new AggregateSsdpDeviceLocator(includeIpv4: true, includeIpv6: false, adapterFilter: null, logger: null);
            var devices = await locator.SearchAsync(SearchTarget, timeout, cancellationToken);
            foreach (var device in devices)
            {
                if (device.DescriptionLocation is { } location)
                {
                    locations.Add(location);
                }
            }
        }
        catch (ArgumentException)
        {
            // No IPv4 adapter usable for multicast exists.
        }

        locations.Add(WellKnownLocation);

        using var httpClient = new HttpClient { Timeout = timeout, MaxResponseContentBufferSize = Tr64Description.MaximumBytes };
        var results = new Dictionary<string, FritzBoxDiscoveryResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var location in locations.DistinctBy(location => location.Host, StringComparer.OrdinalIgnoreCase))
        {
            if (await DescribeAsync(httpClient, location, cancellationToken) is { } result)
            {
                results.TryAdd(result.Udn ?? result.HostAddress, result);
            }
        }

        return results.Values.ToArray();
    }

    /// <returns>The router at a description location, or null when none answers there.</returns>
    internal static async Task<FritzBoxDiscoveryResult?> DescribeAsync(HttpClient httpClient, Uri location, CancellationToken cancellationToken)
    {
        try
        {
            var description = Tr64Description.Parse(await httpClient.GetStringAsync(location, cancellationToken));
            return new FritzBoxDiscoveryResult(location.Host, description.FriendlyName, description.ModelName, description.Udn);
        }
        catch (Exception exception) when ((exception is HttpRequestException or XmlException or TaskCanceledException) && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}
