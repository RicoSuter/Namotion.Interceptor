using System;
using System.Buffers;
using System.Collections.Generic;
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
    private const int SendBufferShrinkThreshold = 256 * 1024;
    private const int MaxPendingUpdates = 1000;

    private readonly System.Net.WebSockets.WebSocket _webSocket;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly IWebSocketSerializer _serializer = JsonWebSocketSerializer.Instance;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ArrayBufferWriter<byte> _sendBuffer = new(4096);
    private readonly long _maxMessageSize;
    private readonly TimeSpan _helloTimeout;
    private readonly Queue<(byte[] Message, long Sequence)> _pendingUpdates = new();
    private volatile bool _welcomeSent;
    private long _welcomeSequence;

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
                _webSocket, _maxMessageSize, _helloTimeout, cancellationToken).ConfigureAwait(false);

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

            var (messageType, payloadStart, payloadLength) = _serializer.DeserializeMessageEnvelope(result.MessageBytes.Span);
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

    public async Task SendWelcomeAsync(SubjectUpdate initialState, long sequence, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var welcome = new WelcomePayload
            {
                Version = 1,
                Format = WebSocketFormat.Json,
                State = initialState,
                Sequence = sequence
            };

            _sendBuffer.Clear();
            _serializer.SerializeMessageTo(_sendBuffer, MessageType.Welcome, welcome);
            await _webSocket.SendAsync(_sendBuffer.WrittenMemory, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);

            MaybeShrinkSendBuffer();

            // Mark welcome as sent and flush any queued pre-serialized updates.
            // Only send updates with sequence > welcomeSequence, since the Welcome
            // snapshot already includes all changes up through welcomeSequence.
            // Store welcomeSequence so SendUpdateAsync can also filter late-arriving
            // broadcasts whose sequence was already included in the snapshot.
            _welcomeSequence = sequence;
            _welcomeSent = true;
            while (_pendingUpdates.TryDequeue(out var pending))
            {
                if (pending.Sequence > sequence)
                {
                    await _webSocket.SendAsync(pending.Message, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task SendUpdateAsync(ReadOnlyMemory<byte> serializedMessage, long sequence, CancellationToken cancellationToken)
    {
        // Acquire lock BEFORE checking _welcomeSent to prevent TOCTOU race with SendWelcomeAsync.
        // Without this, an update could be enqueued after SendWelcomeAsync has already drained the queue.
        if (!IsConnected || Volatile.Read(ref _disposed) == 1) return;

        try
        {
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (!_welcomeSent)
            {
                if (_pendingUpdates.Count >= MaxPendingUpdates)
                {
                    if (Interlocked.Increment(ref _consecutiveSendFailures) == 1)
                    {
                        _logger.LogWarning("Pending update queue full ({MaxPending} messages) for client {ConnectionId}, dropping messages until cleanup", MaxPendingUpdates, ConnectionId);
                    }

                    return; // drop message; zombie detection will clean up
                }

                _pendingUpdates.Enqueue((serializedMessage.ToArray(), sequence));
                return;
            }

            if (!IsConnected) return;

            // Skip updates whose sequence was already included in the Welcome snapshot.
            // This handles the race where BroadcastUpdateAsync increments _sequence,
            // then HandleClientAsync reads that sequence into the Welcome snapshot,
            // and then the broadcast sends the update to this now-welcomed connection.
            if (sequence <= Volatile.Read(ref _welcomeSequence)) return;

            await _webSocket.SendAsync(serializedMessage, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            Interlocked.Exchange(ref _consecutiveSendFailures, 0);
        }
        catch (ObjectDisposedException)
        {
            // Connection disposed during send
        }
        catch (WebSocketException ex)
        {
            Interlocked.Increment(ref _consecutiveSendFailures);
            _logger.LogWarning(ex, "Failed to send Update to client {ConnectionId}", ConnectionId);
        }
        finally
        {
            try { _sendLock.Release(); }
            catch (ObjectDisposedException) { }
        }
    }

    public Task SendErrorAsync(ErrorPayload error, CancellationToken cancellationToken)
    {
        return SendAsync(MessageType.Error, error, trackFailures: false, cancellationToken);
    }

    public Task SendHeartbeatAsync(ReadOnlyMemory<byte> serializedMessage, CancellationToken cancellationToken)
    {
        // Skip heartbeats until Welcome has been sent to avoid confusing clients
        if (!_welcomeSent) return Task.CompletedTask;
        return SendPreSerializedAsync(serializedMessage, trackFailures: true, cancellationToken);
    }

    private Task SendAsync<T>(MessageType messageType, T payload, bool trackFailures, CancellationToken cancellationToken)
    {
        var serializedMessage = _serializer.SerializeMessage(messageType, payload);
        return SendPreSerializedAsync(serializedMessage, trackFailures, cancellationToken);
    }

    private async Task SendPreSerializedAsync(ReadOnlyMemory<byte> serializedMessage, bool trackFailures, CancellationToken cancellationToken)
    {
        if (!IsConnected || Volatile.Read(ref _disposed) == 1) return;

        try
        {
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (!IsConnected) return;

            await _webSocket.SendAsync(serializedMessage, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);

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
            _logger.LogWarning(ex, "Failed to send pre-serialized message to client {ConnectionId}", ConnectionId);
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
            using var result = await WebSocketMessageReader.ReadMessageAsync(_webSocket, _maxMessageSize, linkedCts.Token).ConfigureAwait(false);

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

            var (messageType, payloadStart, payloadLength) = _serializer.DeserializeMessageEnvelope(result.MessageBytes.Span);
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
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error during WebSocket close for {ConnectionId}, aborting", ConnectionId);
                try { _webSocket.Abort(); }
                catch (ObjectDisposedException) { }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        await _cts.CancelAsync().ConfigureAwait(false);
        try { _webSocket.Abort(); }
        catch (ObjectDisposedException) { }
        _webSocket.Dispose();
        _sendLock.Dispose();
        _cts.Dispose();
    }

    private void MaybeShrinkSendBuffer()
    {
        if (_sendBuffer is { Capacity: > SendBufferShrinkThreshold, WrittenCount: < SendBufferShrinkThreshold / 4 })
        {
            _sendBuffer = new ArrayBufferWriter<byte>(4096);
        }
    }
}
