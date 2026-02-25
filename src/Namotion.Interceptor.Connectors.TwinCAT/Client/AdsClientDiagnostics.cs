using TwinCAT.Ads;

namespace Namotion.Interceptor.Connectors.TwinCAT.Client;

/// <summary>
/// Provides diagnostic information about the ADS client connection state.
/// </summary>
public class AdsClientDiagnostics
{
    private readonly IAdsClientDiagnosticsSource _source;

    internal AdsClientDiagnostics(IAdsClientDiagnosticsSource source)
    {
        _source = source;
    }

    /// <summary>
    /// Gets the current PLC state.
    /// </summary>
    public AdsState? State => _source.CurrentState;

    /// <summary>
    /// Gets whether the ADS client is currently connected.
    /// </summary>
    public bool IsConnected => _source.IsConnected;

    /// <summary>
    /// Gets the number of variables using notification mode.
    /// </summary>
    public int NotificationVariableCount => _source.NotificationCount;

    /// <summary>
    /// Gets the number of variables using polling mode.
    /// </summary>
    public int PolledVariableCount => _source.PolledCount;

    /// <summary>
    /// Gets the total reconnection attempts since startup.
    /// </summary>
    public long TotalReconnectionAttempts => _source.TotalReconnectionAttempts;

    /// <summary>
    /// Gets the successful reconnections since startup.
    /// </summary>
    public long SuccessfulReconnections => _source.SuccessfulReconnections;

    /// <summary>
    /// Gets the failed reconnections since startup.
    /// </summary>
    public long FailedReconnections => _source.FailedReconnections;

    /// <summary>
    /// Gets the last successful connection time.
    /// </summary>
    public DateTimeOffset? LastConnectedAt => _source.LastConnectedAt;

    /// <summary>
    /// Gets whether the circuit breaker is currently open.
    /// </summary>
    public bool IsCircuitBreakerOpen => _source.IsCircuitBreakerOpen;

    /// <summary>
    /// Gets the number of times the circuit breaker has tripped.
    /// </summary>
    public long CircuitBreakerTripCount => _source.CircuitBreakerTripCount;
}

/// <summary>
/// Internal interface for providing diagnostic data to <see cref="AdsClientDiagnostics"/>.
/// </summary>
internal interface IAdsClientDiagnosticsSource
{
    /// <summary>Gets the current PLC state.</summary>
    AdsState? CurrentState { get; }

    /// <summary>Gets whether the client is connected.</summary>
    bool IsConnected { get; }

    /// <summary>Gets the number of notification variables.</summary>
    int NotificationCount { get; }

    /// <summary>Gets the number of polled variables.</summary>
    int PolledCount { get; }

    /// <summary>Gets the total reconnection attempts.</summary>
    long TotalReconnectionAttempts { get; }

    /// <summary>Gets the successful reconnection count.</summary>
    long SuccessfulReconnections { get; }

    /// <summary>Gets the failed reconnection count.</summary>
    long FailedReconnections { get; }

    /// <summary>Gets the last successful connection time.</summary>
    DateTimeOffset? LastConnectedAt { get; }

    /// <summary>Gets whether the circuit breaker is open.</summary>
    bool IsCircuitBreakerOpen { get; }

    /// <summary>Gets the circuit breaker trip count.</summary>
    long CircuitBreakerTripCount { get; }
}
