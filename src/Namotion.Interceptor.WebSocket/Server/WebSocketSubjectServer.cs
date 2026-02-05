using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;

namespace Namotion.Interceptor.WebSocket.Server;

/// <summary>
/// Standalone WebSocket server that exposes subject updates to connected clients.
/// Uses Kestrel for cross-platform support without elevation.
/// For embedding in an existing ASP.NET app, use MapWebSocketSubjectHandler extension instead.
/// </summary>
public class WebSocketSubjectServer : BackgroundService, IAsyncDisposable
{
    private readonly WebSocketSubjectHandler _handler;
    private readonly WebSocketServerConfiguration _configuration;
    private readonly ILogger _logger;

    private WebApplication? _app;
    private int _disposed;

    public int ConnectionCount => _handler.ConnectionCount;

    public WebSocketSubjectServer(
        IInterceptorSubject subject,
        WebSocketServerConfiguration configuration,
        ILogger<WebSocketSubjectServer> logger)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        configuration.Validate();

        _handler = new WebSocketSubjectHandler(subject, configuration, logger);
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryDelay = TimeSpan.FromSeconds(5);
        var maxRetryDelay = TimeSpan.FromSeconds(60);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunServerAsync(stoppingToken);
                break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WebSocket server failed. Retrying in {Delay}...", retryDelay);
                await Task.Delay(retryDelay, stoppingToken);

                var jitter = Random.Shared.NextDouble() * 0.1 + 0.95;
                retryDelay = TimeSpan.FromMilliseconds(
                    Math.Min(retryDelay.TotalMilliseconds * 2 * jitter, maxRetryDelay.TotalMilliseconds));
            }
        }
    }

    private async Task RunServerAsync(CancellationToken stoppingToken)
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
                await _handler.HandleClientAsync(webSocket, stoppingToken);
            }
            else
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
            }
        });

        _logger.LogInformation("WebSocket server starting on {Url}{Path}", url, _configuration.Path);

        using var changeQueueProcessor = new ChangeQueueProcessor(
            source: _handler,
            _handler.Context,
            propertyFilter: _handler.IsPropertyIncluded,
            writeHandler: _handler.BroadcastChangesAsync,
            _handler.BufferTime,
            _logger);

        var processorTask = changeQueueProcessor.ProcessAsync(stoppingToken);
        var serverTask = _app.RunAsync(stoppingToken);

        await Task.WhenAll(processorTask, serverTask);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        await _handler.CloseAllConnectionsAsync();

        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        Dispose();
    }
}
