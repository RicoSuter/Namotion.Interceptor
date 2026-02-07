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

        await Task.WhenAll(
            changeQueueProcessor.ProcessAsync(stoppingToken),
            _handler.RunHeartbeatLoopAsync(stoppingToken)).ConfigureAwait(false);
    }
}
