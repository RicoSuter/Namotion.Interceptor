using System.Net;
using System.Net.Http.Json;
using Namotion.Devices.Nuki.Client;
using Namotion.Devices.Nuki.Model;

namespace Namotion.Devices.Nuki;

/// <summary>
/// Finds Nuki bridges and pairs with them.
/// </summary>
public static class NukiBridgeDiscovery
{
    /// <summary>The Nuki discovery service, which lists the bridges seen from the public IP address of the caller.</summary>
    public const string DiscoveryAddress = "https://api.nuki.io/discover/bridges";

    /// <summary>
    /// Lists the bridges that the Nuki discovery service knows for this network. Bridges with discovery disabled are
    /// left out.
    /// </summary>
    public static async Task<IReadOnlyList<NukiDiscoveredBridge>> DiscoverAsync(HttpClient httpClient, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        var response = await httpClient.GetFromJsonAsync(DiscoveryAddress, NukiJsonContext.Default.NukiDiscoveryResponse, timeout.Token);
        return response?.Bridges?
            .Where(bridge => !string.IsNullOrEmpty(bridge.Ip) && bridge.Ip != "0.0.0.0" && bridge.Port > 0)
            .Select(bridge => new NukiDiscoveredBridge(bridge.BridgeId, bridge.Ip!, bridge.Port))
            .ToArray() ?? [];
    }

    /// <summary>
    /// Asks the bridge for its API token. The bridge lights its LED and answers once its button is pressed, within
    /// 30 seconds.
    /// </summary>
    /// <returns>The token, or null when the button was not pressed in time.</returns>
    public static async Task<string?> PairAsync(HttpClient httpClient, string hostAddress, CancellationToken cancellationToken)
    {
        var client = new NukiBridgeClient(httpClient, hostAddress, token: null, NukiTokenMode.Plain);
        var result = await client.AuthAsync(cancellationToken);
        return result.Success && !string.IsNullOrEmpty(result.Token) ? result.Token : null;
    }

    /// <summary>
    /// Finds the token mode a bridge accepts: <see cref="NukiTokenMode.Encrypted"/> when it accepts an encrypted token,
    /// otherwise <see cref="NukiTokenMode.Plain"/> when it accepts the plain one, which a software bridge requires.
    /// </summary>
    public static async Task<NukiTokenMode> DetectTokenModeAsync(HttpClient httpClient, string hostAddress, string token, CancellationToken cancellationToken)
    {
        try
        {
            await new NukiBridgeClient(httpClient, hostAddress, token, NukiTokenMode.Encrypted).GetInfoAsync(cancellationToken);
            return NukiTokenMode.Encrypted;
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.Unauthorized)
        {
            await new NukiBridgeClient(httpClient, hostAddress, token, NukiTokenMode.Plain).GetInfoAsync(cancellationToken);
            return NukiTokenMode.Plain;
        }
    }
}
