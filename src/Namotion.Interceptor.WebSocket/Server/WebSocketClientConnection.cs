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
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly long _maxMessageSize;

    private int _disposed;

    public string ConnectionId { get; } = Guid.NewGuid().ToString("N")[..8];
    public bool IsConnected => _webSocket.State == WebSocketState.Open;

    public WebSocketClientConnection(System.Net.WebSockets.WebSocket webSocket, ILogger logger, long maxMessageSize = 10 * 1024 * 1024)
    {
        _webSocket = webSocket;
        _logger = logger;
        _maxMessageSize = maxMessageSize;
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
        await _sendLock.WaitAsync(cancellationToken);
        try
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
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task SendUpdateAsync(SubjectUpdate update, CancellationToken cancellationToken)
    {
        if (!IsConnected) return;

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            if (!IsConnected) return;  // Re-check after acquiring lock

            var bytes = _serializer.SerializeMessage(MessageType.Update, null, update);
            await _webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "Failed to send update to client {ConnectionId}", ConnectionId);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task SendErrorAsync(ErrorPayload error, CancellationToken cancellationToken)
    {
        if (!IsConnected) return;

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            if (!IsConnected) return;  // Re-check after acquiring lock

            var bytes = _serializer.SerializeMessage(MessageType.Error, null, error);
            await _webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "Failed to send error to client {ConnectionId}", ConnectionId);
        }
        finally
        {
            _sendLock.Release();
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

                if (messageStream.Length + result.Count > _maxMessageSize)
                {
                    _logger.LogWarning("Client {ConnectionId}: Message exceeds maximum size of {MaxSize} bytes", ConnectionId, _maxMessageSize);
                    throw new InvalidOperationException($"Message exceeds maximum size of {_maxMessageSize} bytes");
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
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        await _cts.CancelAsync();

        // Wait for pending sends to release the lock before disposing
        var lockAcquired = false;
        try
        {
            lockAcquired = await _sendLock.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (ObjectDisposedException)
        {
            // Lock already disposed, continue with cleanup
        }

        try
        {
            await CloseAsync();
        }
        finally
        {
            if (lockAcquired)
            {
                _sendLock.Release();
            }

            _sendLock.Dispose();
        }

        _webSocket.Dispose();
        _cts.Dispose();
    }
}
