using Namotion.Interceptor.Connectors.Diagnostics;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// What the OPC UA client reports about its session, on top of the shared source diagnostics.
/// </summary>
/// <remarks>
/// <see cref="ConnectorDiagnostics.IsOperational"/> means the session is usable and no reconnection
/// is in progress. It replaces the former <c>IsConnected</c> and carries the same meaning. True does
/// not mean the model is in sync: while the initial load runs the source state is
/// <see cref="Namotion.Interceptor.Connectors.Monitoring.SourceState.Synchronizing"/>. Read the two
/// together to tell a network outage from a connected client still loading. See
/// docs/connectors-monitoring.md.
/// </remarks>
public sealed class OpcUaClientDiagnostics : SourceDiagnostics
{
    private readonly OpcUaSubjectClientSource _source;

    internal OpcUaClientDiagnostics(OpcUaSubjectClientSource source, SourceMetrics metrics)
        : base(metrics)
    {
        _source = source;
        Reconnects = new ReconnectDiagnostics(source.ReconnectionMetrics);
    }

    /// <summary>
    /// Gets a value indicating whether the client is currently attempting to reconnect. A distinct
    /// sub-state of not being operational, not a second spelling of it.
    /// </summary>
    public bool IsReconnecting => _source.SessionManager?.IsReconnecting ?? false;

    /// <summary>
    /// Gets the current session identifier, or <c>null</c> if there is no session.
    /// </summary>
    public string? SessionId => _source.SessionManager?.CurrentSession?.SessionId?.ToString();

    /// <summary>
    /// Gets the number of active OPC UA subscriptions.
    /// </summary>
    public int SubscriptionCount => _source.SessionManager?.Subscriptions.Count ?? 0;

    /// <summary>
    /// Gets the number of monitored items across all subscriptions.
    /// </summary>
    public int MonitoredItemCount => _source.SessionManager?.SubscriptionManager.MonitoredItems.Count ?? 0;

    /// <summary>
    /// Gets the reconnection history.
    /// </summary>
    public ReconnectDiagnostics Reconnects { get; }

    /// <summary>
    /// Gets polling diagnostics, or <c>null</c> when the polling fallback is off, no session has been
    /// set up yet, or the client is between connect attempts.
    /// </summary>
    /// <remarks>
    /// This reads through the session manager, which is discarded on every failed connect attempt, so
    /// the block is <c>null</c> for the whole retry delay rather than only before the first session.
    /// The underlying totals are owned by the source and survive that, so they reappear at their
    /// previous values once a session exists again.
    /// </remarks>
    public PollingDiagnostics? Polling
    {
        get
        {
            var pollingManager = _source.SessionManager?.PollingManager;
            return pollingManager is not null ? new PollingDiagnostics(pollingManager) : null;
        }
    }

    /// <summary>
    /// Gets read-after-write diagnostics, or <c>null</c> when read-after-write is off, no session has
    /// been set up yet, or the client is between connect attempts.
    /// </summary>
    /// <remarks>
    /// Null between connect attempts for the same reason as <see cref="Polling"/>, and its totals
    /// survive the same way.
    /// </remarks>
    public ReadAfterWriteDiagnostics? ReadAfterWrite
    {
        get
        {
            var manager = _source.SessionManager?.ReadAfterWriteManager;
            return manager is not null ? new ReadAfterWriteDiagnostics(manager) : null;
        }
    }
}

/// <summary>
/// The client's reconnection history. Every counter is monotonic since
/// <see cref="ConnectorDiagnostics.StartTime"/>. <see cref="LastConnectionTime"/> is not a counter
/// and deliberately survives the epoch reset, because it records a discrete past event rather than
/// an amount accumulated during the run.
/// </summary>
public sealed class ReconnectDiagnostics
{
    private readonly ReconnectionMetrics _metrics;

    internal ReconnectDiagnostics(ReconnectionMetrics metrics)
    {
        _metrics = metrics;
    }

    /// <summary>
    /// Gets when the client last established a session, or <c>null</c> if it never has. Records a
    /// discrete past event and survives the disconnection that follows it, which is what the
    /// <c>Last</c> prefix means here.
    /// </summary>
    public DateTimeOffset? LastConnectionTime => _metrics.LastConnectedAt;

    /// <summary>
    /// Gets the number of reconnection attempts started. Once all in-flight attempts have resolved,
    /// this equals <see cref="TotalSucceeded"/> + <see cref="TotalFailed"/> + <see cref="TotalAbandoned"/>.
    /// </summary>
    public long TotalAttempts => _metrics.TotalAttempts;

    /// <summary>
    /// Gets the number of attempts that produced a usable session.
    /// </summary>
    public long TotalSucceeded => _metrics.Successful;

    /// <summary>
    /// Gets the number of attempts that ended with an exception.
    /// </summary>
    public long TotalFailed => _metrics.Failed;

    /// <summary>
    /// Gets the number of attempts that completed without an exception but produced an unusable
    /// result: a null session, a failed transfer, a preserved session after a server restart, a
    /// stall reset, or a kill cancellation.
    /// </summary>
    public long TotalAbandoned => _metrics.Abandoned;
}

/// <summary>
/// The polling fallback used for nodes that do not support subscriptions.
/// </summary>
public sealed class PollingDiagnostics
{
    private readonly Polling.PollingManager _pollingManager;

    internal PollingDiagnostics(Polling.PollingManager pollingManager)
    {
        _pollingManager = pollingManager;
    }

    /// <summary>
    /// Gets the number of items currently being polled.
    /// </summary>
    public int ItemCount => _pollingManager.PollingItemCount;

    /// <summary>
    /// Gets the number of reads that succeeded.
    /// </summary>
    public long TotalSuccessfulReads => _pollingManager.TotalReads;

    /// <summary>
    /// Gets the number of reads that failed.
    /// </summary>
    public long TotalFailedReads => _pollingManager.FailedReads;

    /// <summary>
    /// Gets the number of value changes detected.
    /// </summary>
    public long TotalValueChanges => _pollingManager.ValueChanges;

    /// <summary>
    /// Gets the number of polls whose duration exceeded the polling interval.
    /// </summary>
    public long TotalSlowPolls => _pollingManager.SlowPolls;

    /// <summary>
    /// Gets the number of times the circuit breaker has tripped.
    /// </summary>
    public long TotalCircuitBreakerTrips => _pollingManager.CircuitBreakerTrips;

    /// <summary>
    /// Gets whether the circuit breaker is currently open.
    /// </summary>
    public bool IsCircuitBreakerOpen => _pollingManager.IsCircuitOpen;

    /// <summary>
    /// Gets whether the polling loop is currently running. This is a sub-component's own state, not
    /// a second spelling of <see cref="ConnectorDiagnostics.IsOperational"/>, which describes the
    /// connector as a whole.
    /// </summary>
    public bool IsRunning => _pollingManager.IsRunning;
}

/// <summary>
/// The verification reads issued after an outbound write to a discrete property.
/// </summary>
/// <remarks>
/// Every counter here describes a read that follows a write. The block name contains both words, so
/// each member names its noun to keep a failed verification read from reading as a failed write.
/// </remarks>
public sealed class ReadAfterWriteDiagnostics
{
    private readonly ReadAfterWrite.ReadAfterWriteManager _manager;

    internal ReadAfterWriteDiagnostics(ReadAfterWrite.ReadAfterWriteManager manager)
    {
        _manager = manager;
    }

    /// <summary>
    /// Gets the number of pending verification reads.
    /// </summary>
    public int PendingReads => _manager.PendingReadCount;

    /// <summary>
    /// Gets the number of verification reads scheduled.
    /// </summary>
    public long TotalScheduledReads => _manager.Metrics.Scheduled;

    /// <summary>
    /// Gets the number of verification reads executed.
    /// </summary>
    public long TotalExecutedReads => _manager.Metrics.Executed;

    /// <summary>
    /// Gets the number of scheduled verification reads replaced by a subsequent write.
    /// </summary>
    public long TotalCoalescedReads => _manager.Metrics.Coalesced;

    /// <summary>
    /// Gets the number of verification reads that failed.
    /// </summary>
    public long TotalFailedReads => _manager.Metrics.Failed;
}
