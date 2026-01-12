using System;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.WebSocket.Protocol;
using Namotion.Interceptor.WebSocket.Serialization;

namespace Namotion.Interceptor.WebSocket.Client;

/// <summary>
/// WebSocket client source that connects to a WebSocket server and synchronizes subjects.
/// </summary>
public sealed class WebSocketSubjectClientSource : BackgroundService, ISubjectSource, IAsyncDisposable
{
    private readonly IInterceptorSubject _subject;
    private readonly WebSocketClientConfiguration _configuration;
    private readonly ILogger _logger;
    private readonly ISubjectUpdateProcessor[] _processors;
    private readonly IWsSerializer _serializer = new JsonWsSerializer();

    private ClientWebSocket? _webSocket;
    private SubjectPropertyWriter? _propertyWriter;
    private SubjectUpdate? _initialState;
    private CancellationTokenSource? _receiveCts;
    private TaskCompletionSource? _receiveLoopCompleted;
    private SourceOwnershipManager? _ownership;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private volatile bool _isStarted;
    private int _disposed;

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

        configuration.Validate();
    }

    public async Task<IDisposable?> StartListeningAsync(SubjectPropertyWriter propertyWriter, CancellationToken cancellationToken)
    {
        _propertyWriter = propertyWriter;

        await ConnectAsync(cancellationToken);

        _isStarted = true;

        return new ConnectionLifetime(async () =>
        {
            if (_webSocket?.State == WebSocketState.Open)
            {
                try
                {
                    using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client closing", closeCts.Token);
                }
                catch (OperationCanceledException)
                {
                    _webSocket.Abort();
                }
                catch { }
            }
        });
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        // Clean up any previous connection
        if (_receiveCts is not null)
        {
            await _receiveCts.CancelAsync();
            _receiveCts.Dispose();
        }
        _webSocket?.Dispose();

        _receiveCts = new CancellationTokenSource();
        _receiveLoopCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _webSocket = new ClientWebSocket();

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(_configuration.ConnectTimeout);

        _logger.LogInformation("Connecting to WebSocket server at {Uri}", _configuration.ServerUri);
        await _webSocket.ConnectAsync(_configuration.ServerUri!, connectCts.Token);

        // Send Hello
        var hello = new HelloPayload { Version = 1, Format = WsFormat.Json };
        var helloBytes = _serializer.SerializeMessage(MessageType.Hello, null, hello);
        await _webSocket.SendAsync(helloBytes, WebSocketMessageType.Text, true, cancellationToken);

        // Receive Welcome (handling fragmentation for large state)
        var buffer = new byte[64 * 1024];
        using var messageStream = new MemoryStream();

        WebSocketReceiveResult result;
        do
        {
            result = await _webSocket.ReceiveAsync(buffer, cancellationToken);
            messageStream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        var messageBytes = messageStream.ToArray();
        var (messageType, _, payloadBytes) = _serializer.DeserializeMessageEnvelope(messageBytes);

        if (messageType != MessageType.Welcome)
        {
            throw new InvalidOperationException($"Expected Welcome message, got {messageType}");
        }

        var welcome = _serializer.Deserialize<WelcomePayload>(payloadBytes.Span);
        _initialState = welcome.State;

        _logger.LogInformation("Connected to WebSocket server");

        // Start receive loop (signals _receiveLoopCompleted when done)
        _ = ReceiveLoopAsync(_receiveCts.Token);
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
            _subject.ApplySubjectUpdateFromSource(_initialState, this, factory);

            // Claim ownership of all properties matching the path provider
            ClaimPropertyOwnership();
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

        // Create ownership manager if not already created
        _ownership ??= new SourceOwnershipManager(this);

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

        if (_webSocket?.State != WebSocketState.Open)
        {
            _logger.LogWarning("WebSocket not open, cannot send {Count} changes", changes.Length);
            return WriteResult.Failure(changes, new InvalidOperationException("WebSocket is not connected"));
        }

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            var update = SubjectUpdate.CreatePartialUpdateFromChanges(_subject, changes.Span, _processors);
            var bytes = _serializer.SerializeMessage(MessageType.Update, null, update);
            _logger.LogDebug("Sending {ByteCount} bytes ({SubjectCount} subjects) to server",
                bytes.Length, update.Subjects.Count);
            await _webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
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
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        using var messageStream = new MemoryStream();

        try
        {
            while (!cancellationToken.IsCancellationRequested && _webSocket?.State == WebSocketState.Open)
            {
                try
                {
                    messageStream.SetLength(0);

                    // Receive complete message (handling fragmentation)
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _webSocket.ReceiveAsync(buffer, cancellationToken);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            _logger.LogInformation("Server closed connection");
                            return;
                        }

                        messageStream.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    var messageBytes = messageStream.ToArray();
                    var (messageType, _, payloadBytes) = _serializer.DeserializeMessageEnvelope(messageBytes);

                    switch (messageType)
                    {
                        case MessageType.Update:
                            var update = _serializer.Deserialize<SubjectUpdate>(payloadBytes.Span);
                            HandleUpdate(update);
                            break;

                        case MessageType.Error:
                            var error = _serializer.Deserialize<ErrorPayload>(payloadBytes.Span);
                            _logger.LogWarning("Received error from server: {Code} - {Message}", error.Code, error.Message);
                            break;
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
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing received message");
                }
            }
        }
        finally
        {
            // Signal that receive loop has completed (for reconnection handling)
            _receiveLoopCompleted?.TrySetResult();
        }
    }

    private void HandleUpdate(SubjectUpdate update)
    {
        if (_propertyWriter is null) return;

        _propertyWriter.Write(
            (update, _subject, this, _configuration.SubjectFactory ?? DefaultSubjectFactory.Instance),
            static state => state._subject.ApplySubjectUpdateFromSource(state.update, state.Item3, state.Item4));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait until StartListeningAsync has been called
        while (!_isStarted && !stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(100, stoppingToken);
        }

        // Monitor connection and handle reconnection with exponential backoff
        var reconnectDelay = _configuration.ReconnectDelay;
        var maxDelay = _configuration.MaxReconnectDelay;

        // Cancel receive loop when stopping
        stoppingToken.Register(() => _receiveCts?.Cancel());

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Wait for receive loop to complete (connection dropped)
                var receiveLoopTask = _receiveLoopCompleted?.Task;
                if (receiveLoopTask is not null)
                {
                    await receiveLoopTask.WaitAsync(stoppingToken);
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
                await Task.Delay(reconnectDelay, stoppingToken);

                try
                {
                    await ConnectAsync(stoppingToken);

                    _logger.LogInformation("WebSocket reconnected successfully");

                    // CompleteInitializationAsync will call LoadInitialStateAsync which applies the initial state,
                    // then replays any buffered updates received during reconnection
                    if (_propertyWriter is not null)
                    {
                        await _propertyWriter.CompleteInitializationAsync(stoppingToken);
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

                    // Exponential backoff with jitter
                    var jitter = Random.Shared.NextDouble() * 0.1 + 0.95; // 0.95 to 1.05
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
            await _receiveCts.CancelAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        // Signal receive loop completion to unblock ExecuteAsync
        _receiveLoopCompleted?.TrySetResult();

        _ownership?.Dispose();
        _sendLock.Dispose();

        if (_receiveCts is not null)
        {
            await _receiveCts.CancelAsync();
            _receiveCts.Dispose();
        }

        if (_webSocket is not null)
        {
            if (_webSocket.State == WebSocketState.Open)
            {
                try
                {
                    using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disposing", closeCts.Token);
                }
                catch (OperationCanceledException)
                {
                    // Close timed out, abort
                    _webSocket.Abort();
                }
                catch { }
            }
            _webSocket.Dispose();
        }

        Dispose();
    }

    private sealed class ConnectionLifetime(Func<Task> onDispose) : IDisposable
    {
        public void Dispose() => onDispose().GetAwaiter().GetResult();
    }
}
