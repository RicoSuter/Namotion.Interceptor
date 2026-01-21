using System;
using System.Buffers;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.IO;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.WebSocket.Protocol;
using Namotion.Interceptor.WebSocket.Serialization;

namespace Namotion.Interceptor.WebSocket.Server;

/// <summary>
/// Represents a single client connection to the WebSocket server.
/// </summary>
internal sealed class WebSocketClientConnection : IAsyncDisposable
{
    private static readonly RecyclableMemoryStreamManager MemoryStreamManager = new();

    private readonly System.Net.WebSockets.WebSocket _webSocket;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly IWebSocketSerializer _serializer = new JsonWebSocketSerializer();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
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
        // Apply timeout for Hello message to prevent clients from holding connection slots
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_helloTimeout);

        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            await using var messageStream = MemoryStreamManager.GetStream();

            // Handle fragmented Hello messages
            WebSocketReceiveResult result;
            do
            {
                result = await _webSocket.ReceiveAsync(buffer, timeoutCts.Token);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                if (messageStream.Length + result.Count > _maxMessageSize)
                {
                    _logger.LogWarning("Client {ConnectionId}: Hello message exceeds maximum size", ConnectionId);
                    return null;
                }

                messageStream.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            // Use GetBuffer() to avoid allocation from ToArray()
            var messageBytes = new ReadOnlySpan<byte>(messageStream.GetBuffer(), 0, (int)messageStream.Length);
            var (messageType, _, payloadBytes) = _serializer.DeserializeMessageEnvelope(messageBytes);

            if (messageType == MessageType.Hello)
            {
                return _serializer.Deserialize<HelloPayload>(payloadBytes.Span);
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
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
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

            var bytes = _serializer.SerializeMessage(MessageType.Update, null, update);
            await _webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);

            Interlocked.Exchange(ref _consecutiveSendFailures, 0);
        }
        catch (ObjectDisposedException)
        {
            // Connection disposed during send
        }
        catch (WebSocketException ex)
        {
            Interlocked.Increment(ref _consecutiveSendFailures);
            _logger.LogWarning(ex, "Failed to send update to client {ConnectionId} (failure count: {FailureCount})",
                ConnectionId, Volatile.Read(ref _consecutiveSendFailures));
        }
        finally
        {
            try { _sendLock.Release(); }
            catch (ObjectDisposedException) { }
        }
    }

    public async Task SendErrorAsync(ErrorPayload error, CancellationToken cancellationToken)
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

            var bytes = _serializer.SerializeMessage(MessageType.Error, null, error);
            await _webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        }
        catch (ObjectDisposedException)
        {
            // Connection disposed during send
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "Failed to send error to client {ConnectionId}", ConnectionId);
        }
        finally
        {
            try { _sendLock.Release(); }
            catch (ObjectDisposedException) { }
        }
    }

    public async Task<SubjectUpdate?> ReceiveUpdateAsync(CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            using var messageStream = MemoryStreamManager.GetStream();

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

            var messageLength = (int)messageStream.Length;
            var messageBytes = new ReadOnlySpan<byte>(messageStream.GetBuffer(), 0, messageLength);
            _logger.LogDebug("Client {ConnectionId}: Received {ByteCount} bytes", ConnectionId, messageLength);

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
