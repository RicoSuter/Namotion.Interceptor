using System.Runtime.CompilerServices;
using HomeBlaze.Abstractions.Discovery;
using Namotion.Devices.Nuki.Client;

namespace Namotion.Devices.Nuki;

/// <summary>
/// Finds Nuki bridges through the Nuki discovery service, which lists the bridges seen from the public IP address of
/// the caller.
/// </summary>
public sealed class NukiBridgeDiscoverer : SubjectDiscoverer<NukiBridge, NukiDiscoveredBridge>
{
    private readonly IHttpClientFactory _httpClientFactory;

    public NukiBridgeDiscoverer(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<NukiDiscoveredBridge> DiscoverAsync(
        DiscoveryScan scan, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var httpClient = _httpClientFactory.CreateClient();
        foreach (var bridge in await NukiBridgeDiscovery.DiscoverAsync(httpClient, cancellationToken))
        {
            yield return bridge;
        }
    }

    /// <inheritdoc />
    protected override bool Matches(NukiBridge existing, NukiDiscoveredBridge result) =>
        IsSameBridge(existing.HostAddress, result);

    internal static bool IsSameBridge(string? hostAddress, NukiDiscoveredBridge result) =>
        NukiBridgeClient.TryParseHostAddress(hostAddress, out var host, out var port, out _) &&
        NukiBridgeClient.TryParseHostAddress(result.HostAddress, out var resultHost, out var resultPort, out _) &&
        port == resultPort &&
        string.Equals(host, resultHost, StringComparison.OrdinalIgnoreCase);
}
