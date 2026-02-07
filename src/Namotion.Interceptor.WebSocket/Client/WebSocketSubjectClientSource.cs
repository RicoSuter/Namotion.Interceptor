using System;
using System.Buffers;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IO;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.WebSocket.Internal;
using Namotion.Interceptor.WebSocket.Protocol;
using Namotion.Interceptor.WebSocket.Serialization;

namespace Namotion.Interceptor.WebSocket.Client;

/// <summary>
/// WebSocket client source that connects to a WebSocket server and synchronizes subjects.
/// </summary>
public sealed class WebSocketSubjectClientSource : BackgroundService, ISubjectSource, IAsyncDisposable
{
    private static readonly RecyclableMemoryStreamManager MemoryStreamManager = new();

    private readonly IInterceptorSubject _subject;
    private readonly WebSocketClientConfiguration _configuration;
    private readonly ILogger _logger;
    private readonly ISubjectUpdateProcessor[] _processors;
    private readonly IWebSocketSerializer _serializer = JsonWebSocketSerializer.Instance;
    private readonly ArrayBufferWriter<byte> _sendBuffer = new(4096);

    private ClientWebSocket? _webSocket;
    private volatile SubjectPropertyWriter? _propertyWriter;
    private SubjectUpdate? _initialState;
    private CancellationTokenSource? _receiveCts;
    private TaskCompletionSource? _receiveLoopCompleted;
    private readonly SourceOwnershipManager _ownership;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);

    private volatile bool _isStarted;
    private int _disposed;
    private CancellationTokenRegistration _stoppingTokenRegistration;

    public IInterceptorSubject RootSubject => _subject;
    public int WriteBatchSize => _configuration.WriteBatchSize;

    public WebSocketSubjectClientSource(
        IInterceptorSubject subject,
        WebSocketClientConfiguration configuration,
        ILogger<WebSocketSubjectClientSource> logger)
    {
        _subject = subject ?? throw new ArgumentNullException(nameof(subject));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _processors = configuration.Processors ?? [];
        _ownership = new SourceOwnershipManager(this);

        configuration.Validate();
    }

    public async Task<IDisposable?> StartListeningAsync(SubjectPropertyWriter propertyWriter, CancellationToken cancellationToken)
    {
        _propertyWriter = propertyWriter;

        await ConnectAsync(cancellationToken).ConfigureAwait(false);

        _isStarted = true;

        var capturedSocket = _webSocket;
        return new ConnectionLifetime(async () =>
        {
            if (capturedSocket?.State == WebSocketState.Open)
            {
                try
                {
                    using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await capturedSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client closing", closeCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    capturedSocket.Abort();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while closing WebSocket connection");
                }
            }
        });
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await _connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ConnectCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        // Clean up any previous connection
        if (_receiveCts is not null)
        {
            await _receiveCts.CancelAsync().ConfigureAwait(false);

            // Wait for receive loop to exit before disposing socket
            var previousReceiveLoop = _receiveLoopCompleted?.Task;
            if (previousReceiveLoop is not null)
            {
                try
                {
                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await previousReceiveLoop.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("Receive loop did not complete within timeout during reconnection");
                }
            }

            var oldCts = _receiveCts;
            _receiveCts = null;
            oldCts.Dispose();
        }

        // Now safe to dispose socket
        _webSocket?.Dispose();

        var receiveLoopStarted = false;
        _receiveCts = new CancellationTokenSource();
        _receiveLoopCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            _webSocket = new ClientWebSocket();

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(_configuration.ConnectTimeout);

            _logger.LogInformation("Connecting to WebSocket server at {Uri}", _configuration.ServerUri);
            await _webSocket.ConnectAsync(_configuration.ServerUri!, connectCts.Token).ConfigureAwait(false);

            // Send Hello using reusable buffer
            var hello = new HelloPayload { Version = 1, Format = WebSocketFormat.Json };
            _sendBuffer.Clear();
            _serializer.SerializeMessageTo(_sendBuffer, MessageType.Hello, null, hello);
            await _webSocket.SendAsync(_sendBuffer.WrittenMemory, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);

            // Receive Welcome using shared utility
            using var readResult = await WebSocketMessageReader.ReadMessageAsync(
                _webSocket, _configuration.MaxMessageSize, cancellationToken).ConfigureAwait(false);

            if (readResult.IsCloseMessage)
            {
                throw new InvalidOperationException("Server closed connection during handshake");
            }

            if (readResult.ExceededMaxSize)
            {
                throw new InvalidOperationException($"Message exceeds maximum size of {_configuration.MaxMessageSize} bytes");
            }

            if (!readResult.Success)
            {
                throw new InvalidOperationException("Failed to receive Welcome message");
            }

            var (messageType, _, payloadStart, payloadLength) = _serializer.DeserializeMessageEnvelope(readResult.MessageBytes.Span);
            if (messageType == MessageType.Error)
            {
                var error = _serializer.Deserialize<ErrorPayload>(readResult.MessageBytes.Span.Slice(payloadStart, payloadLength));
                throw new InvalidOperationException($"Server returned error during handshake: [{error.Code}] {error.Message}");
            }

            if (messageType != MessageType.Welcome)
            {
                throw new InvalidOperationException($"Expected Welcome message, got {messageType}");
            }

            var welcome = _serializer.Deserialize<WelcomePayload>(readResult.MessageBytes.Span.Slice(payloadStart, payloadLength));
            if (welcome.Version != 1)
            {
                throw new InvalidOperationException($"Unsupported server protocol version {welcome.Version}. Client supports version 1.");
            }

            _initialState = welcome.State;

            _logger.LogInformation("Connected to WebSocket server");

            // Start receive loop (signals _receiveLoopCompleted when done)
            _ = ReceiveLoopAsync(_receiveCts.Token);
            receiveLoopStarted = true;
        }
        finally
        {
            // Signal completion if receive loop wasn't started (to prevent ExecuteAsync from hanging)
            if (!receiveLoopStarted)
            {
                _receiveLoopCompleted.TrySetResult();
            }
        }
    }

    public Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken)
    {
        if (_initialState is null)
        {
            return Task.FromResult<Action?>(null);
        }

        return Task.FromResult<Action?>(() =>
        {
            var factory = _configuration.SubjectFactory ?? DefaultSubjectFactory.Instance;
            using (SubjectChangeContext.WithSource(this))
            {
                _subject.ApplySubjectUpdate(_initialState, factory);
            }

            // Claim ownership of all properties matching the path provider
            ClaimPropertyOwnership();

            _initialState = null;
        });
    }

    private void ClaimPropertyOwnership()
    {
        var pathProvider = _configuration.PathProvider;

        var registeredSubject = _subject.TryGetRegisteredSubject();
        if (registeredSubject is null)
        {
            _logger.LogWarning("Subject is not registered. Cannot claim property ownership.");
            return;
        }

        // Get all leaf properties, filtered by PathProvider if configured
        var properties = registeredSubject
            .GetAllProperties()
            .Where(p => !p.HasChildSubjects && (pathProvider is null || pathProvider.IsPropertyIncluded(p)))
            .ToList();

        var claimedCount = 0;
        foreach (var property in properties)
        {
            if (_ownership.ClaimSource(property.Reference))
            {
                claimedCount++;
            }
            else
            {
                _logger.LogWarning(
                    "Property {Subject}.{Property} already owned by another source.",
                    property.Subject.GetType().Name, property.Name);
            }
        }

        _logger.LogInformation("Claimed ownership of {Count} properties for WebSocket sync.", claimedCount);
    }

    public async ValueTask<WriteResult> WriteChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        _logger.LogDebug("WriteChangesAsync called with {Count} changes", changes.Length);

        if (Volatile.Read(ref _disposed) == 1)
        {
            return WriteResult.Failure(changes, new ObjectDisposedException(nameof(WebSocketSubjectClientSource)));
        }

        try
        {
            await _connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return WriteResult.Failure(changes, new ObjectDisposedException(nameof(WebSocketSubjectClientSource)));
        }

        try
        {
            if (Volatile.Read(ref _disposed) == 1)
            {
                return WriteResult.Failure(changes, new ObjectDisposedException(nameof(WebSocketSubjectClientSource)));
            }

            var webSocket = _webSocket;
            if (webSocket?.State != WebSocketState.Open)
            {
                return WriteResult.Failure(changes, new InvalidOperationException("WebSocket is not connected"));
            }

            var update = SubjectUpdate.CreatePartialUpdateFromChanges(_subject, changes.Span, _processors);
            _sendBuffer.Clear();
            _serializer.SerializeMessageTo(_sendBuffer, MessageType.Update, null, update);
            _logger.LogDebug("Sending {ByteCount} bytes ({SubjectCount} subjects) to server",
                _sendBuffer.WrittenCount, update.Subjects.Count);
            await webSocket.SendAsync(_sendBuffer.WrittenMemory, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Sent update successfully");
            return WriteResult.Success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send update to server");
            return WriteResult.Failure(changes, ex);
        }
        finally
        {
            try
            {
                _connectionLock.Release();
            }
            catch (ObjectDisposedException)
            {
                // Lock was disposed during operation
            }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);

        // Reusable CTS to reduce allocations (reset instead of recreate when possible)
        var timeoutCts = new CancellationTokenSource();
        CancellationTokenSource? linkedCts = null;

        try
        {
            while (!cancellationToken.IsCancellationRequested && _webSocket?.State == WebSocketState.Open)
            {
                try
                {
                    var messageStream = MemoryStreamManager.GetStream();
                    await using (messageStream.ConfigureAwait(false))
                    {
                        // Reset or recreate the timeout CTS for each message
                        if (!timeoutCts.TryReset())
                        {
                            timeoutCts.Dispose();
                            timeoutCts = new CancellationTokenSource();
                        }

                        timeoutCts.CancelAfter(_configuration.ReceiveTimeout);

                        // Create linked token for this message
                        linkedCts?.Dispose();
                        linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                        // Use shared utility for fragmented message receive
                        var readResult = await WebSocketMessageReader.ReadMessageIntoStreamAsync(
                            _webSocket, buffer, messageStream, _configuration.MaxMessageSize, linkedCts.Token).ConfigureAwait(false);

                        if (readResult.IsCloseMessage)
                        {
                            _logger.LogInformation("Server closed connection");
                            return;
                        }

                        if (readResult.ExceededMaxSize)
                        {
                            _logger.LogWarning("Message exceeds maximum size of {MaxSize} bytes", _configuration.MaxMessageSize);
                            throw new InvalidOperationException($"Message exceeds maximum size of {_configuration.MaxMessageSize} bytes");
                        }

                        var messageBytes = new ReadOnlySpan<byte>(messageStream.GetBuffer(), 0, (int)messageStream.Length);
                        var (messageType, _, payloadStart, payloadLength) = _serializer.DeserializeMessageEnvelope(messageBytes);
                        var payloadBytes = messageBytes.Slice(payloadStart, payloadLength);

                        switch (messageType)
                        {
                            case MessageType.Update:
                                var update = _serializer.Deserialize<SubjectUpdate>(payloadBytes);
                                HandleUpdate(update);
                                break;

                            case MessageType.Error:
                                var error = _serializer.Deserialize<ErrorPayload>(payloadBytes);
                                _logger.LogWarning("Received error from server: {Code} - {Message}", error.Code, error.Message);
                                break;
                        }
                    }
                }
                catch (WebSocketException ex)
                {
                    _logger.LogWarning(ex, "WebSocket error in receive loop");
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Normal shutdown
                    break;
                }
                catch (OperationCanceledException)
                {
                    // Receive timeout - connection considered lost
                    _logger.LogWarning("Receive timeout exceeded ({Timeout}), connection considered lost", _configuration.ReceiveTimeout);
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing received message");
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            timeoutCts.Dispose();
            linkedCts?.Dispose();

            // Signal that receive loop has completed (for reconnection handling)
            _receiveLoopCompleted?.TrySetResult();
        }
    }

    private void HandleUpdate(SubjectUpdate update)
    {
        var propertyWriter = _propertyWriter;
        if (propertyWriter is null) return;

        propertyWriter.Write(
            (update, _subject, this, _configuration.SubjectFactory ?? DefaultSubjectFactory.Instance),
            static state =>
            {
                using (SubjectChangeContext.WithSource(state.Item3))
                {
                    state._subject.ApplySubjectUpdate(state.update, state.Item4);
                }
            });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait until StartListeningAsync has been called
        while (!_isStarted && !stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(500, stoppingToken).ConfigureAwait(false);
        }

        // Monitor connection and handle reconnection with exponential backoff
        var reconnectDelay = _configuration.ReconnectDelay;
        var maxDelay = _configuration.MaxReconnectDelay;

        // Cancel receive loop when stopping (store registration for proper disposal)
        _stoppingTokenRegistration = stoppingToken.Register(() =>
        {
            try { _receiveCts?.Cancel(); }
            catch (ObjectDisposedException) { }
        });

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Wait for receive loop to complete (connection dropped)
                var receiveLoopTask = _receiveLoopCompleted?.Task;
                if (receiveLoopTask is not null)
                {
                    await receiveLoopTask.WaitAsync(stoppingToken).ConfigureAwait(false);
                }

                // Connection dropped - check if we should reconnect
                if (stoppingToken.IsCancellationRequested || Volatile.Read(ref _disposed) == 1)
                {
                    break;
                }

                _logger.LogWarning("WebSocket connection lost. Attempting reconnection in {Delay}...", reconnectDelay);

                // Start buffering changes during reconnection
                _propertyWriter?.StartBuffering();

                // Wait before reconnecting
                await Task.Delay(reconnectDelay, stoppingToken).ConfigureAwait(false);

                try
                {
                    await ConnectAsync(stoppingToken).ConfigureAwait(false);

                    _logger.LogInformation("WebSocket reconnected successfully");

                    // CompleteInitializationAsync will call LoadInitialStateAsync which applies the initial state,
                    // then replays any buffered updates received during reconnection
                    if (_propertyWriter is not null)
                    {
                        await _propertyWriter.CompleteInitializationAsync(stoppingToken).ConfigureAwait(false);
                    }

                    // Success - reset reconnect delay
                    reconnectDelay = _configuration.ReconnectDelay;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to reconnect to WebSocket server");

                    // Exponential backoff with equal jitter (0.5 to 1.0) to decorrelate reconnection attempts
                    var jitter = Random.Shared.NextDouble() * 0.5 + 0.5;
                    reconnectDelay = TimeSpan.FromMilliseconds(
                        Math.Min(reconnectDelay.TotalMilliseconds * 2 * jitter, maxDelay.TotalMilliseconds));
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in WebSocket connection monitoring");
            }
        }

        // Cancel receive loop on exit
        if (_receiveCts is not null)
        {
            await _receiveCts.CancelAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        // Cancel all operations
        await _stoppingTokenRegistration.DisposeAsync().ConfigureAwait(false);
        _receiveLoopCompleted?.TrySetResult();

        if (_receiveCts is not null)
        {
            await _receiveCts.CancelAsync().ConfigureAwait(false);
            _receiveCts.Dispose();
        }

        // Wait for any pending connection operations to complete before disposing the lock
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _connectionLock.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            _connectionLock.Release();
        }
        catch (OperationCanceledException)
        {
            // Timeout waiting for lock - proceed with disposal anyway
            _logger.LogWarning("Timeout waiting for connection lock during disposal");
        }
        catch (ObjectDisposedException)
        {
            // Already disposed
        }

        // Clean up resources
        _ownership.Dispose();
        _webSocket?.Abort();
        _webSocket?.Dispose();
        _connectionLock.Dispose();

        Dispose();
    }

    private sealed class ConnectionLifetime(Func<Task> onDispose) : IDisposable, IAsyncDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            // Blocking call in sync path for correctness
            try { onDispose().GetAwaiter().GetResult(); }
            catch { /* Best effort */ }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try { await onDispose().ConfigureAwait(false); }
            catch { /* Best effort */ }
        }
    }
}