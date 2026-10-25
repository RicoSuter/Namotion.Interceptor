using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;

namespace Namotion.Interceptor.WebSocket.Server;

/// <summary>
/// Background service that processes subject changes and broadcasts them via WebSocket.
/// Used in embedded mode where the WebSocket endpoint is mapped into an existing ASP.NET app.
/// Automatically restarts on transient faults.
/// </summary>
public sealed class WebSocketSubjectChangeProcessor : BackgroundService
{
    private readonly WebSocketSubjectHandler _handler;
    private readonly ILogger _logger;

    // Created by the start and taken by the execution, which then owns it.
    private ChangeQueueProcessor? _startProcessor;

    public WebSocketSubjectChangeProcessor(
        WebSocketSubjectHandler handler,
        ILogger<WebSocketSubjectChangeProcessor> logger)
    {
        _handler = handler;
        _logger = logger;
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // Subscribed here rather than in ExecuteAsync, which the platform may run after StartAsync has
        // returned: a client welcomed in between would miss every change made before the subscription.
        Interlocked.Exchange(ref _startProcessor, _handler.CreateChangeQueueProcessor(_logger))?.Dispose();
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var changeQueueProcessor =
                    Interlocked.Exchange(ref _startProcessor, null) ?? _handler.CreateChangeQueueProcessor(_logger);

                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

                var processorTask = changeQueueProcessor.ProcessAsync(linkedCts.Token);
                var heartbeatTask = _handler.RunHeartbeatLoopAsync(linkedCts.Token);

                // When either task completes (normally or faulted), cancel the other
                // to prevent Task.WhenAll from blocking forever.
                var firstCompleted = await Task.WhenAny(processorTask, heartbeatTask).ConfigureAwait(false);
                await linkedCts.CancelAsync().ConfigureAwait(false);
                await Task.WhenAll(processorTask, heartbeatTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Change processor faulted, restarting in 5 seconds");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        // The execution takes the start's subscription only when its loop runs, which a cancelled
        // start skips, so this is where an untaken one is released.
        base.Dispose();
        Interlocked.Exchange(ref _startProcessor, null)?.Dispose();
    }
}
