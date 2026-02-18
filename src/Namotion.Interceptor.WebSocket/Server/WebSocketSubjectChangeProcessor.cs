using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;

namespace Namotion.Interceptor.WebSocket.Server;

/// <summary>
/// Background service that processes subject changes and broadcasts them via WebSocket.
/// Used in embedded mode where the WebSocket endpoint is mapped into an existing ASP.NET app.
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
        using var changeQueueProcessor = new ChangeQueueProcessor(
            source: _handler,
            _handler.Context,
            propertyFilter: _handler.IsPropertyIncluded,
            writeHandler: _handler.BroadcastChangesAsync,
            _handler.BufferTime,
            _logger);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        var processorTask = changeQueueProcessor.ProcessAsync(linkedCts.Token);
        var heartbeatTask = _handler.RunHeartbeatLoopAsync(linkedCts.Token);

        var tasks = new[] { processorTask, heartbeatTask };
        var completed = await Task.WhenAny(tasks).ConfigureAwait(false);

        // Always cancel the other task when either completes (e.g., heartbeat loop
        // returns immediately when disabled, or a task faults).
        await linkedCts.CancelAsync().ConfigureAwait(false);

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}
