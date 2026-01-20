namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Provides diagnostic information about the OPC UA client connection state.
/// Thread-safe for reading current values.
/// </summary>
public class OpcUaClientDiagnostics
{
    private readonly OpcUaSubjectClientSource _source;

    internal OpcUaClientDiagnostics(OpcUaSubjectClientSource source)
    {
        _source = source;
    }

    /// <summary>
    /// Gets a value indicating whether the client is currently connected to the server.
    /// </summary>
    public bool IsConnected => _source.SessionManager?.IsConnected ?? false;

    /// <summary>
    /// Gets a value indicating whether the client is currently attempting to reconnect.
    /// </summary>
    public bool IsReconnecting => _source.SessionManager?.IsReconnecting ?? false;

    /// <summary>
    /// Gets the current session identifier, or null if not connected.
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
    /// Gets the total number of reconnection attempts (both successful and failed).
    /// </summary>
    public long TotalReconnectionAttempts => _source.TotalReconnectionAttempts;

    /// <summary>
    /// Gets the number of successful reconnections.
    /// </summary>
    public long SuccessfulReconnections => _source.SuccessfulReconnections;

    /// <summary>
    /// Gets the number of failed reconnection attempts.
    /// </summary>
    public long FailedReconnections => _source.FailedReconnections;

    /// <summary>
    /// Gets the timestamp of the last successful connection, or null if never connected.
    /// </summary>
    public DateTimeOffset? LastConnectedAt => _source.LastConnectedAt;

    /// <summary>
    /// Gets the number of items being polled (fallback for nodes without subscription support).
    /// </summary>
    public int PollingItemCount => _source.SessionManager?.PollingManager?.PollingItemCount ?? 0;

    /// <summary>
    /// Gets polling diagnostics, or null if polling is disabled.
    /// </summary>
    public PollingDiagnostics? Polling
    {
        get
        {
            var pollingManager = _source.SessionManager?.PollingManager;
            return pollingManager is not null ? new PollingDiagnostics(pollingManager) : null;
        }
    }
}

/// <summary>
/// Provides diagnostic information about the polling fallback mechanism.
/// </summary>
public class PollingDiagnostics
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
    /// Gets the total number of successful read operations.
    /// </summary>
    public long TotalReads => _pollingManager.TotalReads;

    /// <summary>
    /// Gets the total number of failed read operations.
    /// </summary>
    public long FailedReads => _pollingManager.FailedReads;

    /// <summary>
    /// Gets the total number of value changes detected.
    /// </summary>
    public long ValueChanges => _pollingManager.ValueChanges;

    /// <summary>
    /// Gets the number of slow polls (poll duration exceeded interval).
    /// </summary>
    public long SlowPolls => _pollingManager.SlowPolls;

    /// <summary>
    /// Gets whether the circuit breaker is currently open.
    /// </summary>
    public bool IsCircuitBreakerOpen => _pollingManager.IsCircuitOpen;

    /// <summary>
    /// Gets the total number of circuit breaker trips.
    /// </summary>
    public long CircuitBreakerTrips => _pollingManager.CircuitBreakerTrips;

    /// <summary>
    /// Gets whether the polling loop is currently running.
    /// </summary>
    public bool IsRunning => _pollingManager.IsRunning;
}
