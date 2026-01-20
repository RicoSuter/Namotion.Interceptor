using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.WebSocket.Server;

/// <summary>
/// Handles WebSocket client connections and broadcasts subject updates.
/// Used by both standalone server and embedded endpoint modes.
/// </summary>
public class WebSocketSubjectHandler
{
    private readonly IInterceptorSubject _subject;
    private readonly WebSocketServerConfiguration _configuration;
    private readonly ILogger _logger;
    private readonly ISubjectUpdateProcessor[] _processors;
    private readonly ConcurrentDictionary<string, WebSocketClientConnection> _connections = new();
    private int _connectionCount;

    public IInterceptorSubjectContext Context { get; }
    public int ConnectionCount => Volatile.Read(ref _connectionCount);

    public WebSocketSubjectHandler(
        IInterceptorSubject subject,
        WebSocketServerConfiguration configuration,
        ILogger logger)
    {
        _subject = subject ?? throw new ArgumentNullException(nameof(subject));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Context = subject.Context;
        _processors = configuration.Processors ?? [];
    }

    public async Task HandleClientAsync(System.Net.WebSockets.WebSocket webSocket, CancellationToken stoppingToken)
    {
        // Atomically check and increment connection count
        var newCount = Interlocked.Increment(ref _connectionCount);
        if (newCount > _configuration.MaxConnections)
        {
            Interlocked.Decrement(ref _connectionCount);
            _logger.LogWarning("Maximum connections ({MaxConnections}) reached, rejecting client", _configuration.MaxConnections);
            await webSocket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.PolicyViolation, "Server at capacity", CancellationToken.None);
            return;
        }

        var connection = new WebSocketClientConnection(webSocket, _logger, _configuration.MaxMessageSize);

        try
        {
            // Receive Hello
            var hello = await connection.ReceiveHelloAsync(stoppingToken);
            if (hello is null)
            {
                _logger.LogWarning("Client {ConnectionId}: No Hello received, closing", connection.ConnectionId);
                await connection.CloseAsync("No Hello received");
                return;
            }

            _logger.LogInformation("Client {ConnectionId} connected, sending Welcome...", connection.ConnectionId);

            // Send Welcome with initial state
            var initialState = SubjectUpdate.CreateCompleteUpdate(_subject, _processors);
            await connection.SendWelcomeAsync(initialState, stoppingToken);

            _logger.LogInformation("Client {ConnectionId}: Welcome sent, waiting for updates...", connection.ConnectionId);

            // Register connection
            _connections[connection.ConnectionId] = connection;

            // Handle incoming updates
            await ReceiveUpdatesAsync(connection, stoppingToken);

            _logger.LogDebug("Client {ConnectionId}: ReceiveUpdatesAsync returned normally", connection.ConnectionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling client {ConnectionId}", connection.ConnectionId);
        }
        finally
        {
            _connections.TryRemove(connection.ConnectionId, out _);
            Interlocked.Decrement(ref _connectionCount);
            await connection.DisposeAsync();
            _logger.LogInformation("Client {ConnectionId} disconnected", connection.ConnectionId);
        }
    }

    private async Task ReceiveUpdatesAsync(WebSocketClientConnection connection, CancellationToken stoppingToken)
    {
        _logger.LogDebug("Client {ConnectionId}: Starting receive loop (IsConnected={IsConnected})",
            connection.ConnectionId, connection.IsConnected);

        while (!stoppingToken.IsCancellationRequested && connection.IsConnected)
        {
            var update = await connection.ReceiveUpdateAsync(stoppingToken);
            if (update is null)
            {
                _logger.LogWarning("Client {ConnectionId}: Received null update, exiting loop", connection.ConnectionId);
                break;
            }

            try
            {
                var factory = _configuration.SubjectFactory ?? DefaultSubjectFactory.Instance;
                using (SubjectChangeContext.WithSource(this))
                {
                    _subject.ApplySubjectUpdate(update, factory);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error applying update from client {ConnectionId}", connection.ConnectionId);

                await connection.SendErrorAsync(new Protocol.ErrorPayload
                {
                    Code = Protocol.ErrorCode.InternalError,
                    Message = ex.Message
                }, stoppingToken);
            }
        }

        _logger.LogDebug("Client {ConnectionId}: Exited receive loop (Cancelled={Cancelled}, IsConnected={IsConnected})",
            connection.ConnectionId, stoppingToken.IsCancellationRequested, connection.IsConnected);
    }

    public async ValueTask BroadcastChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        if (changes.Length == 0 || _connections.IsEmpty) return;

        var batchSize = _configuration.WriteBatchSize;
        if (batchSize <= 0 || changes.Length <= batchSize)
        {
            // Single batch
            var update = SubjectUpdate.CreatePartialUpdateFromChanges(_subject, changes.Span, _processors);
            var tasks = _connections.Values.Select(c => c.SendUpdateAsync(update, cancellationToken));
            await Task.WhenAll(tasks);
        }
        else
        {
            // Multiple batches
            for (var i = 0; i < changes.Length; i += batchSize)
            {
                var currentBatchSize = Math.Min(batchSize, changes.Length - i);
                var batch = changes.Slice(i, currentBatchSize);
                var update = SubjectUpdate.CreatePartialUpdateFromChanges(_subject, batch.Span, _processors);
                var tasks = _connections.Values.Select(c => c.SendUpdateAsync(update, cancellationToken));
                await Task.WhenAll(tasks);
            }
        }
    }

    public bool IsPropertyIncluded(RegisteredSubjectProperty property)
    {
        return _configuration.PathProvider?.IsPropertyIncluded(property) ?? true;
    }

    public TimeSpan BufferTime => _configuration.BufferTime;

    public async ValueTask CloseAllConnectionsAsync()
    {
        // Drain all connections atomically
        var connectionsToClose = new System.Collections.Generic.List<WebSocketClientConnection>();
        while (!_connections.IsEmpty)
        {
            foreach (var key in _connections.Keys.ToList())
            {
                if (_connections.TryRemove(key, out var connection))
                {
                    connectionsToClose.Add(connection);
                }
            }
        }

        // Close all in parallel
        await Task.WhenAll(connectionsToClose.Select(async connection =>
        {
            try
            {
                await connection.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error closing connection {ConnectionId}", connection.ConnectionId);
            }
        }));
    }
}
