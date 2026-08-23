namespace Namotion.Interceptor.Connectors.Diagnostics;

/// <summary>
/// What a source reports on top of <see cref="ConnectorDiagnostics"/>.
/// </summary>
public class SourceDiagnostics : ConnectorDiagnostics
{
    private readonly SourceMetrics _metrics;

    /// <summary>
    /// Initializes a new instance of the <see cref="SourceDiagnostics"/> class.
    /// </summary>
    public SourceDiagnostics(SourceMetrics metrics)
        : base(metrics)
    {
        _metrics = metrics;

        OutboundRetries = new QueueDiagnostics(metrics.OutboundRetries);
        InboundBuffer = new QueueDiagnostics(metrics.InboundBuffer);
    }

    /// <summary>
    /// Gets how many properties this source currently owns. A gauge, not a counter: it rises as the
    /// source claims properties and falls as subjects detach.
    /// </summary>
    public int ClaimedPropertyCount => _metrics.ClaimedPropertyCount;

    /// <summary>
    /// Gets the queue of outbound writes awaiting retry. A growing depth means the external system
    /// is rejecting writes.
    /// </summary>
    /// <remarks>
    /// At capacity 0 the depth stays 0; failed and owned connect-window writes are discarded and
    /// included in <see cref="QueueDiagnostics.TotalDropped"/>.
    /// </remarks>
    public QueueDiagnostics OutboundRetries { get; }

    /// <summary>
    /// Gets the buffer of inbound updates held while the initial state loads. A growing depth means
    /// an initial load is still in progress.
    /// </summary>
    /// <remarks>
    /// <see cref="QueueDiagnostics.TotalDropped"/> here counts buffered updates thrown away when a
    /// connect attempt was abandoned before its load completed. Those discards are deliberate rather
    /// than data loss, and a rising total signals reconnect thrash.
    /// </remarks>
    public QueueDiagnostics InboundBuffer { get; }
}
