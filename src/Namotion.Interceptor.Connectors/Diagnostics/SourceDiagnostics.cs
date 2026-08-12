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
    /// source claims properties and falls as subjects detach. The individual claims and releases are
    /// on the source monitoring event stream.
    /// </summary>
    public int ClaimedPropertyCount => _metrics.ClaimedPropertyCount;

    /// <summary>
    /// Gets the queue of outbound writes awaiting retry. A growing depth means the external system
    /// is rejecting writes.
    /// </summary>
    /// <remarks>
    /// When the source is configured without a retry queue this block reports a capacity of 0 and a
    /// depth of 0, while <see cref="QueueDiagnostics.TotalDropped"/> still rises: without a queue,
    /// failed writes are discarded directly and are attributed here.
    /// <para>
    /// In that configuration the total is a floor rather than the whole loss. The larger loss path is
    /// the connect-window drain, which discards every captured write because without a queue there is
    /// nothing to park them in, and which is deliberately uncounted: the drain is unfiltered, so
    /// counting only this source's share of it costs an ownership check per change on a path that runs
    /// only when the queue is disabled, and the configuration already says those writes are being
    /// thrown away.
    /// </para>
    /// </remarks>
    public QueueDiagnostics OutboundRetries { get; }

    /// <summary>
    /// Gets the buffer of inbound updates held while the initial state loads. A growing depth means
    /// an initial load is still in progress.
    /// </summary>
    /// <remarks>
    /// <see cref="QueueDiagnostics.TotalDropped"/> here counts buffered updates thrown away when a
    /// connect attempt was abandoned before its load completed. Those discards are deliberate rather
    /// than data loss, because applying a superseded snapshot would be wrong. The number is useful
    /// as the only signal of how often initial loads are being superseded, which is reconnect thrash.
    /// </remarks>
    public QueueDiagnostics InboundBuffer { get; }
}
