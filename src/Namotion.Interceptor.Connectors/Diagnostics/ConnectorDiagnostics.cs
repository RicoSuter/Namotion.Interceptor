namespace Namotion.Interceptor.Connectors.Diagnostics;

/// <summary>
/// What a connector reports about the transport it drives. Read-only, lock-free, and no getter throws.
/// </summary>
/// <remarks>
/// This answers "what is the transport doing". Whether the model can be trusted is a separate
/// question answered by <see cref="ISubjectSource.State"/>, which describes the inbound direction
/// only. Read them together to tell a network outage from a connected source still loading: the
/// first is <see cref="IsOperational"/> false, the second is <see cref="IsOperational"/> true with a
/// state of <see cref="Monitoring.SourceState.Synchronizing"/>. See docs/connectors-monitoring.md.
/// </remarks>
public class ConnectorDiagnostics
{
    private readonly ConnectorMetrics _metrics;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectorDiagnostics"/> class.
    /// </summary>
    public ConnectorDiagnostics(ConnectorMetrics metrics)
    {
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));

        Throughput = new ThroughputDiagnostics(metrics.Incoming, metrics.Outgoing);
        OutboundChanges = new QueueDiagnostics(metrics.OutboundChanges);
    }

    /// <summary>
    /// Gets a value indicating whether the transport is up and serving. What that means is defined
    /// by each connector and documented on its own diagnostics type. It does not mean the model is
    /// in sync: see the remarks on this type.
    /// </summary>
    public bool IsOperational => _metrics.IsOperational;

    /// <summary>
    /// Gets when <see cref="IsOperational"/> last changed, or <c>null</c> if the connector has never
    /// started. Moves whenever the flag moves, so the pair reads as "up since T" or "down since T".
    /// </summary>
    public DateTimeOffset? OperationalChangeTime => _metrics.OperationalChangeTime;

    /// <summary>
    /// Gets the most recent error in either direction, or <c>null</c> if there has been none.
    /// Sticky: it survives recovery and is only cleared by a restart.
    /// </summary>
    public Exception? LastError => _metrics.LastError;

    /// <summary>
    /// Gets when the connector's current run began, or <c>null</c> if it has never started. This is
    /// the epoch every <c>Total</c> counter below is measured from. It does not move when the
    /// transport reconnects, only when the connector itself is stopped and started.
    /// </summary>
    public DateTimeOffset? StartTime => _metrics.StartTime;

    /// <summary>
    /// Gets the change rates in each direction.
    /// </summary>
    public ThroughputDiagnostics Throughput { get; }

    /// <summary>
    /// Gets the outbound change queue: subject changes waiting to be written to the external system.
    /// A growing depth means changes are produced faster than they can be flushed.
    /// </summary>
    public QueueDiagnostics OutboundChanges { get; }
}
