using System.Collections.Immutable;

namespace Namotion.Interceptor.Connectors.Diagnostics;

/// <summary>
/// Write side of the diagnostics every connector reports. Created and owned by the connector and
/// never reachable through <see cref="ISubjectConnector"/>, so only the connector itself can move
/// its liveness or record its errors.
/// </summary>
public class ConnectorMetrics
{
    private sealed record Liveness(bool IsOperational, long ChangeTicks, bool IsStopped);

    private Liveness _liveness = new(false, 0, false);
    private long _startTicks;
    private Exception? _lastError;
    private ImmutableArray<IResettableMetrics> _resettables = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectorMetrics"/> class.
    /// </summary>
    /// <param name="incoming">Counts changes flowing into the subject tree, or <c>null</c> if this connector does not measure that.</param>
    /// <param name="outgoing">Counts changes flowing out of the subject tree, or <c>null</c> if this connector does not measure that.</param>
    public ConnectorMetrics(ThroughputCounter? incoming = null, ThroughputCounter? outgoing = null)
    {
        Incoming = incoming;
        Outgoing = outgoing;
    }

    /// <summary>
    /// Gets the incoming throughput counter, for the connector to feed. <c>null</c> when this
    /// connector does not measure the direction.
    /// </summary>
    public ThroughputCounter? Incoming { get; }

    /// <summary>
    /// Gets the outgoing throughput counter, for the connector to feed. <c>null</c> when this
    /// connector does not measure the direction.
    /// </summary>
    public ThroughputCounter? Outgoing { get; }

    /// <summary>
    /// Gets the metrics of the outbound change queue that carries subject changes to the external system.
    /// </summary>
    public QueueMetrics OutboundChanges { get; } = new();

    /// <summary>
    /// Opens a new counter epoch: stamps a fresh start time, clears the last error and resets every
    /// <c>Total</c> counter, including those of registered hoisted metrics.
    /// </summary>
    /// <remarks>
    /// Deliberately not idempotent. Called once per <c>ExecuteAsync</c> entry, so a host stop and
    /// start moves the epoch while a transport reconnect inside the connector's own loop does not.
    /// </remarks>
    public void MarkStarted()
    {
        Interlocked.Exchange(ref _startTicks, DateTimeOffset.UtcNow.UtcTicks);
        Volatile.Write(ref _lastError, null);
        ResetTotals();

        foreach (var resettable in _resettables)
        {
            resettable.Reset();
        }
    }

    /// <summary>
    /// Enrolls metrics the connector owns outside this object into the <see cref="MarkStarted"/> reset.
    /// </summary>
    /// <remarks>
    /// Register before the connector starts, and never once per reconnect. There is no deregistration
    /// counterpart, so a per-reconnect registration would grow the list without bound and reset the
    /// same instance once per registration. A resettable enrolled after <see cref="MarkStarted"/> also
    /// carries its counts from before the epoch into it, which breaks the rule that a <c>Total</c>
    /// counts only since the connector's start time.
    /// </remarks>
    public void RegisterResettable(IResettableMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);

        ImmutableInterlocked.Update(ref _resettables, static (current, item) => current.Add(item), metrics);
    }

    /// <summary>
    /// Reports that the connector is now serving. Ignored once <see cref="MarkStopped"/> has run.
    /// </summary>
    public void MarkOperational() => SetOperational(true, terminal: false);

    /// <summary>
    /// Reports that the connector is no longer serving but may recover.
    /// </summary>
    public void MarkNotOperational() => SetOperational(false, terminal: false);

    /// <summary>
    /// Reports that the connector has stopped for good and latches that.
    /// </summary>
    /// <remarks>
    /// Liveness transitions are raised from wherever a connector detects them, which for the OPC UA
    /// client is off the pump thread. Without a terminal rule such a transition can land after the
    /// pump's own exit and resurrect a stopped connector. Mirrors
    /// <see cref="Monitoring.SourceState.Stopped"/>, which is terminal for the same reason.
    /// </remarks>
    public void MarkStopped() => SetOperational(false, terminal: true);

    /// <summary>
    /// Records the most recent failure. Sticky: it survives recovery, because a cleared error erases
    /// the only evidence a transient fault ever happened.
    /// </summary>
    public void ReportError(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);

        Volatile.Write(ref _lastError, error);
    }

    internal bool IsOperational => Volatile.Read(ref _liveness).IsOperational;

    internal DateTimeOffset? OperationalChangeTime
    {
        get
        {
            var ticks = Volatile.Read(ref _liveness).ChangeTicks;
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    internal Exception? LastError => Volatile.Read(ref _lastError);

    internal DateTimeOffset? StartTime
    {
        get
        {
            var ticks = Interlocked.Read(ref _startTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    private protected virtual void ResetTotals() => OutboundChanges.Reset();

    private void SetOperational(bool isOperational, bool terminal)
    {
        var ticks = DateTimeOffset.UtcNow.UtcTicks;

        SpinWait spin = default;
        while (true)
        {
            var current = Volatile.Read(ref _liveness);
            if (current.IsStopped && !terminal)
            {
                return;
            }

            var stopped = current.IsStopped || terminal;
            if (current.IsOperational == isOperational && current.IsStopped == stopped)
            {
                return;
            }

            // The flag and its timestamp are swapped as one value, so every read of the record is
            // internally consistent and the latch stays implementable without a lock. The timestamp
            // moves only when the flag does, so latching the terminal bit on a connector that was
            // never operational does not invent a transition.
            //
            // ticks is sampled before the loop, so a thread preempted between that sample and its
            // successful exchange would otherwise stamp a moment older than a transition another
            // thread already recorded, and the timestamp would move backwards. Clamping to the
            // snapshot being replaced keeps it monotonic.
            var updated = current.IsOperational == isOperational
                ? current with { IsStopped = stopped }
                : new Liveness(isOperational, Math.Max(ticks, current.ChangeTicks), stopped);

            // Reference equality, not Liveness's record-generated value equality:
            // Interlocked.CompareExchange compares by reference, so a genuinely failed exchange can
            // hand back a different instance that happens to be value-equal to the one this loop read,
            // and an == check would read that failure as success and silently drop the update.
            if (ReferenceEquals(Interlocked.CompareExchange(ref _liveness, updated, current), current))
            {
                return;
            }

            spin.SpinOnce();
        }
    }
}
