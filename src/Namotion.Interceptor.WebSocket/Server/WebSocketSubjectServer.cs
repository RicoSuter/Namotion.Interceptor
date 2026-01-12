using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.WebSocket.Server;

/// <summary>
/// WebSocket server that exposes subject updates to connected clients.
/// Uses Kestrel for cross-platform support without elevation.
/// </summary>
public class WebSocketSubjectServer : BackgroundService, IAsyncDisposable
{
    private readonly IInterceptorSubject _subject;
    private readonly IInterceptorSubjectContext _context;
    private readonly WebSocketServerConfiguration _configuration;
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<string, WebSocketClientConnection> _connections = new();
    private readonly ISubjectUpdateProcessor[] _processors;

    private WebApplication? _app;
    private int _disposed;

    public int ConnectionCount => _connections.Count;

    public WebSocketSubjectServer(
        IInterceptorSubject subject,
        WebSocketServerConfiguration configuration,
        ILogger<WebSocketSubjectServer> logger)
    {
        _subject = subject ?? throw new ArgumentNullException(nameof(subject));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _context = subject.Context;
        _processors = configuration.Processors ?? [];

        configuration.Validate();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var builder = WebApplication.CreateSlimBuilder();

        var url = _configuration.BindAddress is not null
            ? $"http://{_configuration.BindAddress}:{_configuration.Port}"
            : $"http://localhost:{_configuration.Port}";

        builder.WebHost.UseUrls(url);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        _app = builder.Build();
        _app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30)
        });

        _app.Map(_configuration.Path, async context =>
        {
            if (context.WebSockets.IsWebSocketRequest)
            {
                var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                await HandleClientAsync(webSocket, stoppingToken);
            }
            else
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
            }
        });

        _logger.LogInformation("WebSocket server starting on {Url}{Path}", url, _configuration.Path);

        using var changeQueueProcessor = new ChangeQueueProcessor(
            source: this,
            _context,
            propertyFilter: IsPropertyIncluded,
            writeHandler: BroadcastChangesAsync,
            _configuration.BufferTime,
            _logger);

        var processorTask = changeQueueProcessor.ProcessAsync(stoppingToken);
        var serverTask = _app.RunAsync(stoppingToken);

        await Task.WhenAll(processorTask, serverTask);
    }

    private async Task HandleClientAsync(System.Net.WebSockets.WebSocket webSocket, CancellationToken stoppingToken)
    {
        var connection = new WebSocketClientConnection(webSocket, _logger);

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
                _subject.ApplySubjectUpdateFromSource(update, this, factory);
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

    private async ValueTask BroadcastChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
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

    private bool IsPropertyIncluded(RegisteredSubjectProperty property)
    {
        return _configuration.PathProvider?.IsPropertyIncluded(property) ?? true;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        foreach (var connection in _connections.Values)
        {
            await connection.DisposeAsync();
        }
        _connections.Clear();

        if (_app is not null)
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await _app.StopAsync(stopCts.Token);
            }
            catch (OperationCanceledException)
            {
                // Stop timed out
            }
            await _app.DisposeAsync();
        }

        Dispose();
    }
}
