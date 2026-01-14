using System;
using System.Buffers;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.WebSocket.Protocol;
using Namotion.Interceptor.WebSocket.Serialization;

namespace Namotion.Interceptor.WebSocket.Server;

/// <summary>
/// Represents a single client connection to the WebSocket server.
/// </summary>
internal sealed class WebSocketClientConnection : IAsyncDisposable
{
    private readonly System.Net.WebSockets.WebSocket _webSocket;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly IWebSocketSerializer _serializer = new JsonWebSocketSerializer();

    public string ConnectionId { get; } = Guid.NewGuid().ToString("N")[..8];
    public bool IsConnected => _webSocket.State == WebSocketState.Open;

    public WebSocketClientConnection(System.Net.WebSockets.WebSocket webSocket, ILogger logger)
    {
        _webSocket = webSocket;
        _logger = logger;
    }

    public async Task<HelloPayload?> ReceiveHelloAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var result = await _webSocket.ReceiveAsync(buffer, cancellationToken);

        if (result.MessageType == WebSocketMessageType.Close)
        {
            return null;
        }

        try
        {
            var (messageType, _, payloadBytes) = _serializer.DeserializeMessageEnvelope(buffer.AsSpan(0, result.Count));
            if (messageType == MessageType.Hello)
            {
                return _serializer.Deserialize<HelloPayload>(payloadBytes.Span);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse Hello message from client {ConnectionId}", ConnectionId);
        }

        return null;
    }

    public async Task SendWelcomeAsync(SubjectUpdate initialState, CancellationToken cancellationToken)
    {
        var welcome = new WelcomePayload
        {
            Version = 1,
            Format = WebSocketFormat.Json,
            State = initialState
        };

        var bytes = _serializer.SerializeMessage(MessageType.Welcome, null, welcome);
        await _webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    public async Task SendUpdateAsync(SubjectUpdate update, CancellationToken cancellationToken)
    {
        if (!IsConnected) return;

        try
        {
            var bytes = _serializer.SerializeMessage(MessageType.Update, null, update);
            await _webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "Failed to send update to client {ConnectionId}", ConnectionId);
        }
    }

    public async Task SendErrorAsync(ErrorPayload error, CancellationToken cancellationToken)
    {
        if (!IsConnected) return;

        try
        {
            var bytes = _serializer.SerializeMessage(MessageType.Error, null, error);
            await _webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "Failed to send error to client {ConnectionId}", ConnectionId);
        }
    }

    public async Task<SubjectUpdate?> ReceiveUpdateAsync(CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            using var messageStream = new MemoryStream();

            // Receive complete message (handling fragmentation)
            WebSocketReceiveResult result;
            do
            {
                result = await _webSocket.ReceiveAsync(buffer, cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogInformation("Client {ConnectionId}: Received close message", ConnectionId);
                    return null;
                }

                messageStream.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            var messageBytes = messageStream.ToArray();
            _logger.LogDebug("Client {ConnectionId}: Received {ByteCount} bytes", ConnectionId, messageBytes.Length);

            var (messageType, _, payloadBytes) = _serializer.DeserializeMessageEnvelope(messageBytes);

            if (messageType == MessageType.Update)
            {
                var update = _serializer.Deserialize<SubjectUpdate>(payloadBytes.Span);
                _logger.LogDebug("Client {ConnectionId}: Received update with {SubjectCount} subjects",
                    ConnectionId, update.Subjects.Count);
                return update;
            }

            _logger.LogWarning("Received unexpected message type {MessageType} from client {ConnectionId}",
                messageType, ConnectionId);
            return null;
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "Client {ConnectionId}: WebSocket error in receive", ConnectionId);
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async Task CloseAsync(string reason = "Server closing")
    {
        if (_webSocket.State == WebSocketState.Open)
        {
            try
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, closeCts.Token);
            }
            catch (WebSocketException)
            {
                // Ignore close errors
            }
            catch (OperationCanceledException)
            {
                // Close timed out, abort instead
                _webSocket.Abort();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        await CloseAsync();
        _webSocket.Dispose();
        _cts.Dispose();
    }
}
