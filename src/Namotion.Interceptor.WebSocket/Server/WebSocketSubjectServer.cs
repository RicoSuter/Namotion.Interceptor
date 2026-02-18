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
/// On Kill, restarts both the HTTP listener and the processing layer (matching real crash behavior).
/// For embedding in an existing ASP.NET app, use MapWebSocketSubjectHandler extension instead.
/// </summary>
public sealed class WebSocketSubjectServer : BackgroundService, ISubjectConnector, IFaultInjectable, IAsyncDisposable
{
    private readonly WebSocketSubjectHandler _handler;
    private readonly WebSocketServerConfiguration _configuration;
    private readonly ILogger _logger;

    private WebApplication? _app;
    private int _disposed;
    private volatile bool _isForceKill;
    private volatile CancellationTokenSource? _forceKillCts;

    /// <inheritdoc />
    public IInterceptorSubject RootSubject { get; }

    /// <summary>
    /// Gets the number of currently connected WebSocket clients.
    /// </summary>
    public int ConnectionCount => _handler.ConnectionCount;

    /// <summary>
    /// Gets the current broadcast sequence number.
    /// </summary>
    public long CurrentSequence => _handler.CurrentSequence;

    public WebSocketSubjectServer(
        IInterceptorSubject subject,
        WebSocketServerConfiguration configuration,
        ILogger<WebSocketSubjectServer> logger)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        configuration.Validate();

        RootSubject = subject;
        _handler = new WebSocketSubjectHandler(subject, configuration, logger);
        _configuration = configuration;
        _logger = logger;
    }

    /// <inheritdoc />
    async Task IFaultInjectable.InjectFaultAsync(FaultType faultType, CancellationToken cancellationToken)
    {
        switch (faultType)
        {
            case FaultType.Kill:
                _isForceKill = true;
                try { _forceKillCts?.Cancel(); }
                catch (ObjectDisposedException) { /* CTS disposed between loop iterations */ }
                break;

            case FaultType.Disconnect:
                await _handler.CloseAllConnectionsAsync().ConfigureAwait(false);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(faultType), faultType, null);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _forceKillCts = cts;
            var linkedToken = cts.Token;

            try
            {
                // Build a new WebApplication each iteration because IHost doesn't support
                // Start/Stop cycles. On Kill, the entire Kestrel instance is torn down and
                // rebuilt, matching real crash behavior (like MQTT restarts its broker).
                _app = BuildWebApplication(linkedToken, out var listenUrl);

                _logger.LogInformation("WebSocket server starting on {Url}{Path}", listenUrl, _configuration.Path);
                await _app.StartAsync(stoppingToken).ConfigureAwait(false);

                using var changeQueueProcessor = _handler.CreateChangeQueueProcessor(_logger);

                var processorTask = changeQueueProcessor.ProcessAsync(linkedToken);
                var heartbeatTask = _handler.RunHeartbeatLoopAsync(linkedToken);

                try
                {
                    // When either task completes, cancel the other to prevent blocking forever.
                    await Task.WhenAny(processorTask, heartbeatTask).ConfigureAwait(false);
                    await cts.CancelAsync().ConfigureAwait(false);
                    await Task.WhenAll(processorTask, heartbeatTask).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // Kill or one task completed: linkedToken canceled
                }

                // Both tasks completed — either normally (tasks catch OCE internally and
                // return) or via caught OCE above. Check why we stopped:
                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                // linkedToken was canceled (Kill) or unexpected completion — restart.
                if (_isForceKill)
                {
                    _logger.LogWarning("WebSocket server force-killed. Restarting...");
                }
                else
                {
                    _logger.LogWarning("WebSocket server processing completed unexpectedly. Restarting...");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WebSocket server processing failed. Restarting...");
            }
            finally
            {
                await _handler.CloseAllConnectionsAsync().ConfigureAwait(false);

                if (_app is not null)
                {
                    // Use a short timeout to avoid the default 30-second ASP.NET graceful
                    // shutdown. Connections are already closed above, so Kestrel should stop
                    // quickly. The timeout is just a safety net.
                    using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try
                    {
                        await _app.StopAsync(shutdownCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Shutdown timed out — DisposeAsync will force-release the port
                    }

                    await _app.DisposeAsync().ConfigureAwait(false);
                    _app = null;
                }

                _isForceKill = false;
                cts.Dispose();
            }
        }
    }

    private WebApplication BuildWebApplication(CancellationToken requestHandlingToken, out string listenUrl)
    {
        var builder = WebApplication.CreateSlimBuilder();

        listenUrl = _configuration.BindAddress is not null
            ? $"http://{_configuration.BindAddress}:{_configuration.Port}"
            : $"http://localhost:{_configuration.Port}";

        builder.WebHost.UseUrls(listenUrl);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30)
        });

        app.Map(_configuration.Path, async context =>
        {
            if (context.WebSockets.IsWebSocketRequest)
            {
                var webSocket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
                await _handler.HandleClientAsync(webSocket, requestHandlingToken).ConfigureAwait(false);
            }
            else
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
            }
        });

        return app;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        // Stop ExecuteAsync if called directly (not via hosting)
        try
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await StopAsync(stopCts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort stop
        }

        await _handler.CloseAllConnectionsAsync().ConfigureAwait(false);

        if (_app is not null)
        {
            await _app.DisposeAsync().ConfigureAwait(false);
        }

        Dispose();
    }
}
