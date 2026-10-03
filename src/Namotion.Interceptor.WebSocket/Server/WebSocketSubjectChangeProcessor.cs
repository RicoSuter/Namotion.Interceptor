using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Tracking.Change;

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
        : base(logger)
    {
        _handler = handler;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override IInterceptorSubjectContext Context => _handler.Context;

    /// <inheritdoc />
    protected override ChangeQueueProcessor CreateProcessor(PropertyChangeQueueSubscription subscription) =>
        _handler.CreateChangeQueueProcessor(subscription, _logger);

    /// <inheritdoc />
    protected override async Task ProcessAsync(ChangeQueueProcessor processor, CancellationToken cancellationToken)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var processorTask = processor.ProcessAsync(session.Token);
        var heartbeatTask = _handler.RunHeartbeatLoopAsync(session.Token);

        // Cancel the sibling first, or WhenAll below waits on it forever.
        await Task.WhenAny(processorTask, heartbeatTask).ConfigureAwait(false);
        await session.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(processorTask, heartbeatTask).ConfigureAwait(false);
    }
}
