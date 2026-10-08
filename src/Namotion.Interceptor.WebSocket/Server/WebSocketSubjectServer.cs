using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Diagnostics;

namespace Namotion.Interceptor.WebSocket.Server;

/// <summary>
/// Standalone WebSocket server that exposes subject updates to connected clients.
/// Uses Kestrel for cross-platform support without elevation.
/// On Kill, restarts both the HTTP listener and the processing layer.
/// A Kill that arrives between attempts, such as during the restart backoff, has no attempt to cancel
/// and does nothing.
/// For embedding in an existing ASP.NET app, use MapWebSocketSubjectHandler extension instead.
/// </summary>
public sealed class WebSocketSubjectServer : SubjectServerBase, IFaultInjectable, IAsyncDisposable
{
    private readonly WebSocketSubjectHandler _handler;
    private readonly WebSocketServerConfiguration _configuration;
    private readonly ILogger _logger;

    private WebApplication? _app;
    private int _disposed;

    /// <inheritdoc />
    public override IInterceptorSubject RootSubject { get; }

    /// <inheritdoc cref="SubjectConnectorBase.Diagnostics" />
    public override WebSocketServerDiagnostics Diagnostics { get; }

    internal int ConnectionCount => _handler.ConnectionCount;

    internal long CurrentSequence => _handler.CurrentSequence;

    public WebSocketSubjectServer(
        IInterceptorSubject subject,
        WebSocketServerConfiguration configuration,
        ILogger<WebSocketSubjectServer> logger)
        : base(new ConnectorMetrics(), logger)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        configuration.Validate();

        RootSubject = subject;
        Diagnostics = new WebSocketServerDiagnostics(this, Metrics);
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
                await ForceKillCurrentAttemptAsync().ConfigureAwait(false);
                break;

            case FaultType.Disconnect:
                await _handler.CloseAllConnectionsAsync().ConfigureAwait(false);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(faultType), faultType, null);
        }
    }

    /// <inheritdoc />
    protected override ChangeQueueProcessor CreateChangeQueueProcessor(Action<long> dropHandler) =>
        _handler.CreateChangeQueueProcessor(_logger, dropHandler);

    /// <inheritdoc />
    protected override async Task<IAsyncDisposable?> StartServerAsync(ConnectorRunAttempt attempt, CancellationToken stoppingToken)
    {
        var attemptToken = attempt.Token;
        var teardown = new AttemptTeardown(this, attempt);

        try
        {
            // Built per attempt because IHost does not support Start/Stop cycles: a kill tears down and
            // rebuilds the whole Kestrel instance, matching real crash behavior.
            var app = BuildWebApplication(attemptToken, out var listenUrl);
            _app = app;

            _logger.LogInformation("WebSocket server starting on {Url}{Path}", listenUrl, _configuration.Path);
            await app.StartAsync(attemptToken).ConfigureAwait(false);

            teardown.HeartbeatTask = RunHeartbeatAsync(attempt, attemptToken);
            return teardown;
        }
        catch
        {
            await teardown.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task RunHeartbeatAsync(ConnectorRunAttempt attempt, CancellationToken attemptToken)
    {
        try
        {
            await _handler.RunHeartbeatLoopAsync(attemptToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (attemptToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            // Rethrown so the teardown surfaces it and the base records it as the failure.
            throw;
        }
        finally
        {
            // The attempt is still live here: its teardown awaits this task before the attempt is disposed.
            if (!attemptToken.IsCancellationRequested)
            {
                await attempt.CancelAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task StopApplicationAsync()
    {
        // Claimed atomically, because DisposeAsync also races for this app after a stop that timed out,
        // and both winning would dispose it twice.
        var app = Interlocked.Exchange(ref _app, null);
        if (app is null)
        {
            return;
        }

        try
        {
            // Use a short timeout to avoid the default 30-second ASP.NET graceful shutdown. Connections are
            // already closed, so Kestrel should stop quickly.
            using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await app.StopAsync(shutdownCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutdown timed out; the disposal below force-releases the port.
            }
        }
        finally
        {
            // In a finally, because a stop that fails must not skip the disposal: the app still holds the
            // listening port and every later bind would fail.
            await app.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class AttemptTeardown : IAsyncDisposable
    {
        private readonly WebSocketSubjectServer _server;
        private readonly ConnectorRunAttempt _attempt;

        public AttemptTeardown(WebSocketSubjectServer server, ConnectorRunAttempt attempt)
        {
            _server = server;
            _attempt = attempt;
        }

        public Task? HeartbeatTask { get; set; }

        public async ValueTask DisposeAsync()
        {
            try
            {
                // The heartbeat runs until the attempt is cancelled.
                await _attempt.CancelAsync().ConfigureAwait(false);
                if (HeartbeatTask is not null)
                {
                    await HeartbeatTask.ConfigureAwait(false);
                }
            }
            finally
            {
                // Guards the app release documented on StopApplicationAsync: a cancel or heartbeat fault
                // above must not skip it.
                await _server._handler.CloseAllConnectionsAsync().ConfigureAwait(false);
                await _server.StopApplicationAsync().ConfigureAwait(false);
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

        // Claimed atomically, because a stop that timed out can leave the run loop's own teardown
        // still racing for this app, and both winning would dispose it twice.
        var app = Interlocked.Exchange(ref _app, null);
        if (app is not null)
        {
            await app.DisposeAsync().ConfigureAwait(false);
        }

        Dispose();
    }
}
