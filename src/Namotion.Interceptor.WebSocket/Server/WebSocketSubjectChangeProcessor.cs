using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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

    public WebSocketSubjectChangeProcessor(
        WebSocketSubjectHandler handler,
        ILogger<WebSocketSubjectChangeProcessor> logger)
    {
        _handler = handler;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var changeQueueProcessor = _handler.CreateChangeQueueProcessor(_logger);

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
}
