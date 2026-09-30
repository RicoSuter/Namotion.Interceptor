using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;

namespace Namotion.Interceptor.WebSocket.Server;

/// <summary>
/// Background service that processes subject changes and broadcasts them via WebSocket.
/// Used in embedded mode where the WebSocket endpoint is mapped into an existing ASP.NET app.
/// Automatically restarts on transient faults.
/// </summary>
public sealed class WebSocketSubjectChangeProcessor : ChangeQueueBackgroundService
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

    /// <inheritdoc />
    protected override ChangeQueueProcessor CreateProcessor() => _handler.CreateChangeQueueProcessor(_logger);

    /// <inheritdoc />
    protected override TimeSpan? GetRetryDelay(Exception exception)
    {
        _logger.LogError(exception, "Change processor faulted, restarting in 5 seconds");
        return TimeSpan.FromSeconds(5);
    }

    /// <inheritdoc />
    protected override async Task ProcessAsync(ChangeQueueProcessor processor, CancellationToken stoppingToken)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var processorTask = processor.ProcessAsync(session.Token);
        var heartbeatTask = _handler.RunHeartbeatLoopAsync(session.Token);

        // When either task completes, cancel its sibling before observing both outcomes.
        await Task.WhenAny(processorTask, heartbeatTask).ConfigureAwait(false);
        await session.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(processorTask, heartbeatTask).ConfigureAwait(false);

        if (!stoppingToken.IsCancellationRequested)
        {
            RequestRestart();
        }
    }
}
