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

    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Lists the bridges that the Nuki discovery service knows for this network. Bridges with discovery disabled are
    /// left out. The service has 10 seconds to answer.
    /// </summary>
    /// <exception cref="TimeoutException">The discovery service did not answer within 10 seconds.</exception>
    /// <exception cref="HttpRequestException">The discovery service could not be reached or answered with an error.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public static Task<IReadOnlyList<NukiDiscoveredBridge>> DiscoverAsync(HttpClient httpClient, CancellationToken cancellationToken) =>
        DiscoverAsync(httpClient, DiscoveryTimeout, cancellationToken);

    internal static async Task<IReadOnlyList<NukiDiscoveredBridge>> DiscoverAsync(
        HttpClient httpClient, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            using var response = await httpClient.GetAsync(DiscoveryAddress, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token);
            response.EnsureSuccessStatusCode();
            await response.Content.LoadIntoBufferAsync(NukiBridgeClient.MaxResponseSize, timeoutSource.Token);
            var discovery = await response.Content.ReadFromJsonAsync(NukiJsonContext.Default.NukiDiscoveryResponse, timeoutSource.Token);
            return discovery?.Bridges?
                .Where(bridge => IsReachable(bridge.Ip, bridge.Port))
                .Select(bridge => new NukiDiscoveredBridge(bridge.BridgeId, bridge.Ip!, bridge.Port))
                .ToArray() ?? [];
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The Nuki discovery service did not answer within {timeout.TotalSeconds:0} seconds.", exception);
        }
    }

    // The service lists 0.0.0.0 and port 0 for a bridge with discovery disabled.
    private static bool IsReachable(string? ipAddress, int port) =>
        IPAddress.TryParse(ipAddress, out var address) && !address.Equals(IPAddress.Any) && port is >= 1 and <= 65535;

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
    /// Finds the token mode for a bridge: <see cref="NukiTokenMode.Encrypted"/> when it accepts an encrypted token.
    /// When it rejects the encrypted token, its type is read with the plain token: a software bridge, which requires
    /// <see cref="NukiTokenMode.Plain"/>, gives Plain; a hardware bridge gives Encrypted, as it supports encrypted
    /// tokens and most likely rejected the token because its clock is off.
    /// </summary>
    /// <exception cref="HttpRequestException">The bridge rejected the plain token too, or answered with an error.</exception>
    /// <exception cref="TimeoutException">The bridge did not answer in time.</exception>
    public static async Task<NukiTokenMode> DetectTokenModeAsync(HttpClient httpClient, string hostAddress, string token, CancellationToken cancellationToken)
    {
        try
        {
            await new NukiBridgeClient(httpClient, hostAddress, token, NukiTokenMode.Encrypted).GetInfoAsync(cancellationToken);
            return NukiTokenMode.Encrypted;
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.Unauthorized)
        {
            var info = await new NukiBridgeClient(httpClient, hostAddress, token, NukiTokenMode.Plain).GetInfoAsync(cancellationToken);
            return NukiStateMapping.ToBridgeType(info.BridgeType) == NukiBridgeType.Hardware
                ? NukiTokenMode.Encrypted
                : NukiTokenMode.Plain;
        }
    }
}
