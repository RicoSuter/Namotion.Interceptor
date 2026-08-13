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
/// On Kill, restarts both the HTTP listener and the processing layer (matching real crash behavior).
/// A Kill that arrives while the server is between attempts, such as during the restart backoff, has
/// no attempt to cancel and is dropped: the call returns successfully having done nothing.
/// For embedding in an existing ASP.NET app, use MapWebSocketSubjectHandler extension instead.
/// </summary>
public sealed class WebSocketSubjectServer : SubjectConnectorBase, IFaultInjectable, IAsyncDisposable
{
    // Matches the MQTT broker's restart delay. Without it a listener that cannot bind rebuilds and
    // rebinds Kestrel in a tight loop.
    private static readonly TimeSpan RestartBackoff = TimeSpan.FromSeconds(5);

    private readonly WebSocketSubjectHandler _handler;
    private readonly WebSocketServerConfiguration _configuration;
    private readonly ILogger _logger;

    private WebApplication? _app;
    private int _disposed;
    private volatile ConnectorRunAttempt? _currentAttempt;

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
        : base(new ConnectorMetrics())
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
                // With no current attempt the loop is between attempts, so there is nothing to kill and
                // nothing is signalled back: the teardown and the backoff this fault stands for have
                // already happened or are already under way.
                var attempt = _currentAttempt;
                if (attempt is not null)
                {
                    await attempt.ForceKillAsync().ConfigureAwait(false);
                }
                break;

            case FaultType.Disconnect:
                await _handler.CloseAllConnectionsAsync().ConfigureAwait(false);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(faultType), faultType, null);
        }
    }

    /// <inheritdoc />
    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var attempt = new ConnectorRunAttempt(stoppingToken);
            _currentAttempt = attempt;
            var linkedToken = attempt.Token;

            // Set by the catch below only, which is where a listener that cannot start or bind lands.
            // A force-kill and a processing layer that ended without throwing both restart at once: the
            // trade is a few seconds of downtime against no throttle at all, and neither of those two
            // repeats fast enough to need one. A processing layer that faults does back off, because
            // its exception reaches that catch like any other.
            var restartBackoff = TimeSpan.Zero;

            try
            {
                try
                {
                    // Build a new WebApplication each iteration because IHost doesn't support
                    // Start/Stop cycles. On Kill, the entire Kestrel instance is torn down and
                    // rebuilt, matching real crash behavior (like MQTT restarts its broker).
                    _app = BuildWebApplication(linkedToken, out var listenUrl);

                    _logger.LogInformation("WebSocket server starting on {Url}{Path}", listenUrl, _configuration.Path);
                    await _app.StartAsync(stoppingToken).ConfigureAwait(false);
                    Metrics.MarkOperational();

                    using var changeQueueProcessor = _handler.CreateChangeQueueProcessor(_logger);
                    try
                    {
                        // Registered inside the try whose finally releases it, so the next restart can
                        // register its own processor: a second Register while one is still live throws.
                        // A Register that throws has registered nothing, so what the finally then
                        // releases is the foreign registration that was already live, which is how this
                        // attempt's failure still leaves the next one able to register. The embedded
                        // mode's own processor deliberately does not register, so it cannot wire itself
                        // into this server's metrics.
                        Metrics.OutboundChanges.Register(
                            () => changeQueueProcessor.QueueDepth, () => changeQueueProcessor.DropCount, capacity: null);

                        var processorTask = changeQueueProcessor.ProcessAsync(linkedToken);
                        var heartbeatTask = _handler.RunHeartbeatLoopAsync(linkedToken);

                        // The filter below cannot be narrowed to the force-kill the way the OPC UA and
                        // MQTT servers narrow theirs, because cancelling the attempt makes a
                        // cancellation the normal exit path here. The exception is kept instead and
                        // judged below, where an unexpected completion is actually identified.
                        OperationCanceledException? completionCancellation = null;
                        try
                        {
                            // When either task completes, cancel the other to prevent blocking forever.
                            await Task.WhenAny(processorTask, heartbeatTask).ConfigureAwait(false);
                            await attempt.CancelAsync().ConfigureAwait(false);
                            await Task.WhenAll(processorTask, heartbeatTask).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException exception) when (!stoppingToken.IsCancellationRequested)
                        {
                            // Kill or one task completed: linkedToken canceled
                            completionCancellation = exception;
                        }

                        // Both tasks completed, either normally (tasks catch OCE internally and
                        // return) or via caught OCE above. Check why we stopped:
                        if (stoppingToken.IsCancellationRequested)
                        {
                            break;
                        }

                        // linkedToken was canceled (Kill) or completed unexpectedly, so restart.
                        if (attempt.WasForceKilled)
                        {
                            // Deliberately not reported through ReportError: it is an injected fault the
                            // server recovers from by restarting, and handling it here is also what
                            // keeps it from reaching the base class, which would record a cancellation
                            // the stopping token did not cause as a genuine fault.
                            _logger.LogWarning("WebSocket server force-killed. Restarting...");
                        }
                        else
                        {
                            // Neither the host stopping nor an injected fault, so the processing layer
                            // ended on its own. The loop restarts instead of leaving RunAsync, so the
                            // base class never sees it and without this the server would restart with
                            // nothing explaining why.
                            //
                            // Neither task surfaces the cancellation raised above to stop the sibling:
                            // the change processor returns because its dequeue reports the cancellation
                            // rather than throwing it, and the heartbeat loop swallows its own. So on
                            // this path the captured value is normally null and the recorded error
                            // carries no inner exception. It is kept for the case where one of them does
                            // throw, where it names the cancellation that ended the layer.
                            var error = new InvalidOperationException(
                                "WebSocket server processing completed unexpectedly.", completionCancellation);

                            Metrics.ReportError(error);
                            _logger.LogWarning(error, "WebSocket server processing completed unexpectedly. Restarting...");
                        }
                    }
                    finally
                    {
                        // Runs before the using disposes the processor, so no reader can call into a
                        // disposed one. Deregistering with nothing live is a no-op fold.
                        Metrics.OutboundChanges.Deregister();
                    }
                }
                finally
                {
                    // Inside the try the catches below guard, like the MQTT broker's teardown: disposing
                    // the WebApplication disposes its whole service provider, and a singleton that
                    // throws on disposal would otherwise leave RunAsync and end the connector.
                    //
                    // First in the teardown, so a throw further down cannot leave a server that has
                    // stopped accepting connections reporting that it is serving.
                    Metrics.MarkNotOperational();

                    await _handler.CloseAllConnectionsAsync().ConfigureAwait(false);

                    var app = _app;
                    if (app is not null)
                    {
                        // Cleared before the teardown rather than after it, so a dispose that throws
                        // cannot leave a half-torn-down app reachable for the next iteration to
                        // overwrite. The local keeps the disposal below reachable.
                        _app = null;

                        try
                        {
                            // Use a short timeout to avoid the default 30-second ASP.NET graceful
                            // shutdown. Connections are already closed above, so Kestrel should stop
                            // quickly. The timeout is just a safety net.
                            using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                            try
                            {
                                await app.StopAsync(shutdownCts.Token).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                // Shutdown timed out, so DisposeAsync will force-release the port.
                            }
                        }
                        finally
                        {
                            // In a finally, because a stop that fails for any other reason must not
                            // skip the disposal: the app still holds the listening port, the field no
                            // longer points at it, and every later bind would fail.
                            await app.DisposeAsync().ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The base class only sees exceptions that leave RunAsync, and this loop swallows every
                // per-attempt failure. Without this, a server whose listener can never bind reports no
                // error. A failure the stop itself caused is left unrecorded: the clause above only
                // covers the cancellation, not the arbitrary exception a socket torn down mid-stop
                // raises, and recording that would overwrite the genuine fault for good, because
                // LastError is sticky and a stopped server does not start again.
                if (!stoppingToken.IsCancellationRequested)
                {
                    Metrics.ReportError(ex);
                }

                _logger.LogError(ex, "WebSocket server processing failed. Restarting...");
                restartBackoff = RestartBackoff;
            }
            finally
            {
                _currentAttempt = null;
                attempt.Dispose();
            }

            // After the teardown above, so the port is free rather than held for the whole delay.
            if (restartBackoff > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(restartBackoff, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // The delay sits outside every catch above, so a stop landing here would otherwise
                    // leave RunAsync as a cancellation and end the hosted service task canceled.
                    break;
                }
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
