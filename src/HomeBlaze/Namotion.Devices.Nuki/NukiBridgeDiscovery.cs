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
            var bridges = new List<NukiDiscoveredBridge>();
            foreach (var bridge in discovery?.Bridges ?? [])
            {
                // The service lists 0.0.0.0 and port 0 for a bridge with discovery disabled.
                if (IPAddress.TryParse(bridge.Ip, out var address) && !address.Equals(IPAddress.Any) && bridge.Port is >= 1 and <= 65535)
                {
                    bridges.Add(new NukiDiscoveredBridge(bridge.BridgeId, new IPEndPoint(address, bridge.Port).ToString()));
                }
            }

            return bridges;
        }
        // A request that the disposal of the HTTP client cancels did not time out.
        catch (OperationCanceledException exception) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The Nuki discovery service did not answer within {timeout.TotalSeconds:0} seconds.", exception);
        }
    }

    /// <summary>
    /// Asks the bridge for its API token. The bridge lights its LED and answers once its button is pressed, within
    /// 30 seconds.
    /// </summary>
    /// <returns>The token, or null when the button was not pressed in time.</returns>
    public static async Task<string?> PairAsync(string hostAddress, CancellationToken cancellationToken)
    {
        using var httpClient = NukiBridgeHttpClient.Create();
        return await PairAsync(httpClient, hostAddress, cancellationToken);
    }

    internal static async Task<string?> PairAsync(HttpClient httpClient, string hostAddress, CancellationToken cancellationToken)
    {
        var client = new NukiBridgeClient(httpClient, hostAddress, token: null, NukiTokenMode.Plain);
        var result = await client.AuthAsync(cancellationToken);
        return result.Success && !string.IsNullOrEmpty(result.Token) ? result.Token : null;
    }

    /// <summary>
    /// Finds the token mode for a bridge: <see cref="NukiTokenMode.Encrypted"/> when it accepts an encrypted token,
    /// otherwise the mode that its bridge type, read with the plain token, requires, and Encrypted when the bridge
    /// cannot be read. <see cref="NukiTokenModeDetection.Warning"/> is set when the bridge did not confirm the mode.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public static async Task<NukiTokenModeDetection> DetectTokenModeAsync(string hostAddress, string token, CancellationToken cancellationToken)
    {
        using var httpClient = NukiBridgeHttpClient.Create();
        return await DetectTokenModeAsync(httpClient, hostAddress, token, cancellationToken);
    }

    internal static async Task<NukiTokenModeDetection> DetectTokenModeAsync(HttpClient httpClient, string hostAddress, string token, CancellationToken cancellationToken)
    {
        try
        {
            try
            {
                await new NukiBridgeClient(httpClient, hostAddress, token, NukiTokenMode.Encrypted).GetInfoAsync(cancellationToken);
                return new NukiTokenModeDetection(NukiTokenMode.Encrypted, Warning: null);
            }
            catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.Unauthorized)
            {
                var info = await new NukiBridgeClient(httpClient, hostAddress, token, NukiTokenMode.Plain).GetInfoAsync(cancellationToken);
                return NukiStateMapping.ToBridgeType(info.BridgeType) == NukiBridgeType.Hardware
                    ? new NukiTokenModeDetection(
                        NukiTokenMode.Encrypted,
                        "The hardware bridge rejected the encrypted token, probably because its clock is off. Using Encrypted; " +
                        "requests fail until the clock of the bridge is correct, or switch to Plain in the settings.")
                    : new NukiTokenModeDetection(NukiTokenMode.Plain, Warning: null);
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Keeping the paired token spares another press of the bridge button; hardware bridges use Encrypted.
            return new NukiTokenModeDetection(
                NukiTokenMode.Encrypted,
                $"The token mode could not be detected ({exception.Message}). Using Encrypted; switch to Plain in the " +
                "settings for a software bridge.");
        }
    }
}
