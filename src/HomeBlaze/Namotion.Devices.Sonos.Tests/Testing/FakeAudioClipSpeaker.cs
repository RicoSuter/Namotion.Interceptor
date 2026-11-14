using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Namotion.Devices.Sonos.Tests.Testing;

/// <summary>
/// What the fake speaker received from one client: the upgrade request and the first websocket message.
/// </summary>
internal sealed record ReceivedAudioClip(string RequestLine, string? ApiKey, string? SubProtocol, WebSocketMessageType MessageType, string Message);

/// <summary>
/// A loopback audio clip websocket as a Sonos speaker serves it: over TLS with a certificate that no authority issued
/// and that does not name the address. Serves one connection.
/// </summary>
internal sealed class FakeAudioClipSpeaker : IAsyncDisposable
{
    private const string SubProtocol = "v1.api.smartspeaker.audio";
    private const string WebSocketAcceptSuffix = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly X509Certificate2 _certificate;
    private readonly bool _answersClose;
    private readonly TaskCompletionSource<ReceivedAudioClip> _received = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _serving;

    /// <param name="answersClose">False acts as a speaker that keeps the connection open when the client closes it.</param>
    internal FakeAudioClipSpeaker(bool answersClose = true)
    {
        _answersClose = answersClose;
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=not-the-address", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var selfSigned = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        // Loaded from its export: a server cannot use the in-memory key of a fresh certificate on every platform.
        _certificate = X509CertificateLoader.LoadPkcs12(selfSigned.Export(X509ContentType.Pfx), null);
        _listener.Start();
        Endpoint = new Uri($"wss://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/websocket/api");
        _serving = ServeAsync();
    }

    internal Uri Endpoint { get; }

    /// <summary>
    /// Completes when the first message arrived, and fails when the connection ended before.
    /// </summary>
    internal Task<ReceivedAudioClip> Received => _received.Task;

    private async Task ServeAsync()
    {
        try
        {
            using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
            await using var stream = new SslStream(client.GetStream());
            await stream.AuthenticateAsServerAsync(_certificate);

            var headers = await ReadUpgradeRequestAsync(stream);
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(headers["sec-websocket-key"] + WebSocketAcceptSuffix)));
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                $"Sec-WebSocket-Accept: {accept}\r\nSec-WebSocket-Protocol: {SubProtocol}\r\n\r\n"), _stop.Token);

            using var webSocket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, SubProtocol = SubProtocol });
            var buffer = new byte[8192];
            var message = await webSocket.ReceiveAsync(buffer, _stop.Token);
            _received.TrySetResult(new ReceivedAudioClip(
                headers[string.Empty],
                headers.GetValueOrDefault("x-sonos-api-key"),
                headers.GetValueOrDefault("sec-websocket-protocol"),
                message.MessageType,
                Encoding.UTF8.GetString(buffer, 0, message.Count)));

            if (_answersClose)
            {
                await webSocket.ReceiveAsync(buffer, _stop.Token);
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, _stop.Token);
            }
            else
            {
                await Task.Delay(Timeout.Infinite, _stop.Token);
            }
        }
        catch (Exception exception)
        {
            // Also the cancellation of the disposal; a test that still waits for the message then fails with it.
            _received.TrySetException(exception);
        }
    }

    /// <returns>The headers by lower-case name, with the request line under the empty name.</returns>
    private async Task<Dictionary<string, string>> ReadUpgradeRequestAsync(SslStream stream)
    {
        var head = new StringBuilder();
        var one = new byte[1];
        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(one, _stop.Token) == 0)
            {
                throw new IOException("The client closed the connection before its upgrade request ended.");
            }

            head.Append((char)one[0]);
        }

        var lines = head.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var headers = new Dictionary<string, string>(StringComparer.Ordinal) { [string.Empty] = lines[0] };
        foreach (var line in lines.Skip(1))
        {
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            headers[line[..separator].ToLowerInvariant()] = line[(separator + 1)..].Trim();
        }

        return headers;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _serving;
        _stop.Dispose();
        _certificate.Dispose();
    }
}
