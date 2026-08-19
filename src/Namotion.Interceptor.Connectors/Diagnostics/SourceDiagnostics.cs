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
        HeldWrites = new QueueDiagnostics(metrics.HeldWrites);
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
    /// When the source is configured without a retry queue this block reports a capacity of 0 and a
    /// depth of 0, while <see cref="QueueDiagnostics.TotalDropped"/> still rises: failed writes are
    /// then discarded directly and are attributed here. In that configuration the total is a floor
    /// rather than the whole loss, because the connect-window drain discards captured writes without
    /// counting them.
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

    /// <summary>
    /// Gets the set of writes held back because the source refused them for its current connection.
    /// Unlike <see cref="OutboundRetries"/> a held write is not queued for retry: it is still owed to
    /// the source and is released back to the retry queue when the connection is replaced, so this is
    /// not a loss count. A depth that stays above zero across reconnections is a property the source
    /// will not take.
    /// </summary>
    /// <remarks>
    /// The capacity is <c>null</c> because the held set is bounded by the model's property count (one
    /// entry per property) rather than by a configured size. When the source is configured without a
    /// retry queue there is nothing to hold writes back, and this block reports a capacity of 0 and a
    /// depth of 0.
    /// </remarks>
    public QueueDiagnostics HeldWrites { get; }
}
