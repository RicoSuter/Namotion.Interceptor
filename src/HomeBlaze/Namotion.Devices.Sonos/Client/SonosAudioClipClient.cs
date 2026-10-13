using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Namotion.Devices.Sonos.Client;

/// <summary>
/// Queues audio clips over the local websocket API of a speaker, which plays them over the current content.
/// </summary>
internal static class SonosAudioClipClient
{
    private const int Port = 1443;
    private const string SubProtocol = "v1.api.smartspeaker.audio";

    // The key that local clients are known to send; it identifies no account.
    private const string ApiKey = "123e4567-e89b-12d3-a456-426655440000";

    /// <summary>
    /// Queues a clip on a connection of its own, so a connection that failed or that the speaker closed never fails a
    /// later clip. The speaker's answer is not read, so a rejected clip is not reported. The connection is closed
    /// gracefully within <paramref name="closeTimeout"/>, which lets the speaker take the clip first. Throws
    /// <see cref="WebSocketException"/> when the speaker cannot be reached on the websocket port and
    /// <see cref="OperationCanceledException"/> when the token is cancelled before the clip is sent.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer", "S4830", Justification = "The speakers present a certificate that no public authority issued and that does not name their address, so it cannot be validated.")]
    internal static async Task QueueAsync(
        string host, string playerId, Uri soundUri, int volume, TimeSpan closeTimeout, ILogger logger, CancellationToken cancellationToken)
    {
        using var webSocket = new ClientWebSocket();
        webSocket.Options.AddSubProtocol(SubProtocol);
        webSocket.Options.SetRequestHeader("X-Sonos-Api-Key", ApiKey);
        webSocket.Options.RemoteCertificateValidationCallback = static (_, _, _, _) => true;

        await webSocket.ConnectAsync(new Uri($"wss://{host}:{Port}/websocket/api"), cancellationToken);
        await webSocket.SendAsync(CreateCommand(playerId, soundUri, volume), WebSocketMessageType.Binary, endOfMessage: true, cancellationToken);

        using var closing = new CancellationTokenSource(closeTimeout);
        try
        {
            await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, closing.Token);
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
        {
            // The clip is sent; disposing the socket releases a connection that did not close in time.
            logger.LogDebug(exception, "Closing the Sonos audio clip websocket of {Host} failed.", host);
        }
    }

    internal static ReadOnlyMemory<byte> CreateCommand(string playerId, Uri soundUri, int volume)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();

            writer.WriteStartObject();
            writer.WriteString("namespace", "audioClip:1");
            writer.WriteString("command", "loadAudioClip");
            writer.WriteString("playerId", playerId);
            writer.WriteEndObject();

            writer.WriteStartObject();
            writer.WriteString("name", "Namotion.Devices.Sonos");
            writer.WriteString("appId", "com.namotion.devices.sonos");
            writer.WriteString("streamUrl", soundUri.AbsoluteUri);
            writer.WriteNumber("volume", volume);
            writer.WriteEndObject();

            writer.WriteEndArray();
        }

        return buffer.WrittenMemory;
    }
}
