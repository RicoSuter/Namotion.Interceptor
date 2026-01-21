using System;
using System.Buffers;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.WebSocket.Internal;
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
    private readonly IWebSocketSerializer _serializer = JsonWebSocketSerializer.Instance;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ArrayBufferWriter<byte> _sendBuffer = new(4096);
    private readonly long _maxMessageSize;
    private readonly TimeSpan _helloTimeout;

    private int _disposed;
    private int _consecutiveSendFailures;

    public string ConnectionId { get; } = Guid.NewGuid().ToString("N")[..8];
    public bool IsConnected => _webSocket.State == WebSocketState.Open;
    public bool HasRepeatedSendFailures => Volatile.Read(ref _consecutiveSendFailures) >= 3;

    public WebSocketClientConnection(
        System.Net.WebSockets.WebSocket webSocket,
        ILogger logger,
        long maxMessageSize = 10 * 1024 * 1024,
        TimeSpan? helloTimeout = null)
    {
        _webSocket = webSocket;
        _logger = logger;
        _maxMessageSize = maxMessageSize;
        _helloTimeout = helloTimeout ?? TimeSpan.FromSeconds(10);
    }

    public async Task<HelloPayload?> ReceiveHelloAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var result = await WebSocketMessageReader.ReadMessageWithTimeoutAsync(
                _webSocket, _maxMessageSize, _helloTimeout, cancellationToken);

            if (result.IsCloseMessage)
            {
                return null;
            }

            if (result.ExceededMaxSize)
            {
                _logger.LogWarning("Client {ConnectionId}: Hello message exceeds maximum size", ConnectionId);
                return null;
            }

            if (!result.Success)
            {
                return null;
            }

            var (messageType, _, payloadStart, payloadLength) = _serializer.DeserializeMessageEnvelope(result.MessageBytes.Span);

            if (messageType == MessageType.Hello)
            {
                return _serializer.Deserialize<HelloPayload>(result.MessageBytes.Span.Slice(payloadStart, payloadLength));
            }

            _logger.LogWarning("Client {ConnectionId}: Expected Hello, received {MessageType}", ConnectionId, messageType);
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Client {ConnectionId}: Hello timeout exceeded ({Timeout})", ConnectionId, _helloTimeout);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse Hello message from client {ConnectionId}", ConnectionId);
            return null;
        }
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

            _sendBuffer.Clear();
            _serializer.SerializeMessageTo(_sendBuffer, MessageType.Welcome, null, welcome);
            await _webSocket.SendAsync(_sendBuffer.WrittenMemory, WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public Task SendUpdateAsync(SubjectUpdate update, CancellationToken cancellationToken)
    {
        return SendAsync(MessageType.Update, update, trackFailures: true, cancellationToken);
    }

    public Task SendErrorAsync(ErrorPayload error, CancellationToken cancellationToken)
    {
        return SendAsync(MessageType.Error, error, trackFailures: false, cancellationToken);
    }

    private async Task SendAsync<T>(MessageType messageType, T payload, bool trackFailures, CancellationToken cancellationToken)
    {
        if (!IsConnected || Volatile.Read(ref _disposed) == 1) return;

        try
        {
            await _sendLock.WaitAsync(cancellationToken);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (!IsConnected) return;

            _sendBuffer.Clear();
            _serializer.SerializeMessageTo(_sendBuffer, messageType, null, payload);
            await _webSocket.SendAsync(_sendBuffer.WrittenMemory, WebSocketMessageType.Text, true, cancellationToken);

            if (trackFailures)
            {
                Interlocked.Exchange(ref _consecutiveSendFailures, 0);
            }
        }
        catch (ObjectDisposedException)
        {
            // Connection disposed during send
        }
        catch (WebSocketException ex)
        {
            if (trackFailures)
            {
                Interlocked.Increment(ref _consecutiveSendFailures);
            }
            _logger.LogWarning(ex, "Failed to send {MessageType} to client {ConnectionId}", messageType, ConnectionId);
        }
        finally
        {
            try { _sendLock.Release(); }
            catch (ObjectDisposedException) { }
        }
    }

    public async Task<SubjectUpdate?> ReceiveUpdateAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
            using var result = await WebSocketMessageReader.ReadMessageAsync(_webSocket, _maxMessageSize, linkedCts.Token);

            if (result.IsCloseMessage)
            {
                _logger.LogInformation("Client {ConnectionId}: Received close message", ConnectionId);
                return null;
            }

            if (result.ExceededMaxSize)
            {
                _logger.LogWarning("Client {ConnectionId}: Message exceeds maximum size of {MaxSize} bytes", ConnectionId, _maxMessageSize);
                throw new InvalidOperationException($"Message exceeds maximum size of {_maxMessageSize} bytes");
            }

            if (!result.Success)
            {
                return null;
            }

            _logger.LogDebug("Client {ConnectionId}: Received {ByteCount} bytes", ConnectionId, result.MessageBytes.Length);

            var (messageType, _, payloadStart, payloadLength) = _serializer.DeserializeMessageEnvelope(result.MessageBytes.Span);

            if (messageType == MessageType.Update)
            {
                var update = _serializer.Deserialize<SubjectUpdate>(result.MessageBytes.Span.Slice(payloadStart, payloadLength));
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
    }

    public async Task CloseAsync(string reason = "Server closing")
    {
        if (_webSocket.State == WebSocketState.Open)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, cts.Token);
            }
            catch
            {
                _webSocket.Abort();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        await _cts.CancelAsync();
        _webSocket.Abort();
        _webSocket.Dispose();
        _sendLock.Dispose();
        _cts.Dispose();
    }
}
