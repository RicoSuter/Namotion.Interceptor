using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MQTTnet;
using Namotion.Interceptor.Sources.Resilience;

namespace Namotion.Interceptor.Mqtt.Client;

/// <summary>
/// Monitors MQTT client connection health and handles reconnection with exponential backoff.
/// Uses a hybrid approach: events trigger immediate action, but actual reconnection happens in monitoring task.
/// </summary>
internal sealed class MqttConnectionMonitor : IAsyncDisposable
{
    private readonly IMqttClient _client;
    private readonly MqttClientConfiguration _configuration;
    private readonly ILogger _logger;

    private readonly Func<MqttClientOptions> _optionsBuilder;
    private readonly Func<CancellationToken, Task> _onReconnected;
    private readonly Func<Task> _onDisconnected;

    private readonly SemaphoreSlim _reconnectSignal = new(0, 1);
    private readonly CircuitBreaker? _circuitBreaker;

    private int _isReconnecting;
    private int _reconnectingIterations; // Tracks health check iterations while reconnecting (for stall detection)
    private int _disposed;

    public MqttConnectionMonitor(
        IMqttClient client,
        MqttClientConfiguration configuration,
        Func<MqttClientOptions> optionsBuilder,
        Func<CancellationToken, Task> onReconnected,
        Func<Task> onDisconnected,
        ILogger logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _optionsBuilder = optionsBuilder ?? throw new ArgumentNullException(nameof(optionsBuilder));
        _onReconnected = onReconnected ?? throw new ArgumentNullException(nameof(onReconnected));
        _onDisconnected = onDisconnected ?? throw new ArgumentNullException(nameof(onDisconnected));

        // Initialize circuit breaker if enabled
        if (configuration.CircuitBreakerFailureThreshold > 0)
        {
            _circuitBreaker = new CircuitBreaker(
                configuration.CircuitBreakerFailureThreshold,
                configuration.CircuitBreakerCooldown);
        }
    }

    /// <summary>
    /// Signals that a reconnection is needed (called by DisconnectedAsync event handler in MqttSubjectClientSource).
    /// </summary>
    public void SignalReconnectNeeded()
    {
        try
        {
            // Only signal if not already signaled (semaphore maxCount is 1)
            _reconnectSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signaled, ignore
        }
    }

    /// <summary>
    /// Monitors connection health and performs reconnection with exponential backoff, circuit breaker, and stall detection.
    /// This is a blocking method that runs until a cancellation is requested.
    /// Uses hybrid approach: Waits for disconnect event OR periodic health check.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to stop monitoring.</param>
    public async Task MonitorConnectionAsync(CancellationToken cancellationToken)
    {
        var healthCheckInterval = _configuration.HealthCheckInterval;
        var maxDelay = _configuration.MaximumReconnectDelay;
        var stallThreshold = _configuration.ReconnectStallThreshold;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Wait for either: Disconnect event signal OR periodic health check timeout
                var signaled = await _reconnectSignal.WaitAsync(healthCheckInterval, cancellationToken).ConfigureAwait(false);
                if (signaled)
                {
                    _logger.LogWarning("MQTT disconnect event received.");
                    await _onDisconnected().ConfigureAwait(false);
                }
                else
                {
                    // Periodic health check: Use TryPingAsync (recommended by MQTTnet)
                    var isHealthy = _client.IsConnected && await _client.TryPingAsync(cancellationToken).ConfigureAwait(false);
                    if (isHealthy)
                    {
                        // Connection healthy - reset stall detection counter
                        Interlocked.Exchange(ref _reconnectingIterations, 0);
                        continue;
                    }

                    _logger.LogWarning("MQTT health check failed.");
                }

                // Stall detection: Check if reconnection is hung
                if (Volatile.Read(ref _isReconnecting) == 1 && stallThreshold > 0)
                {
                    var iterations = Interlocked.Increment(ref _reconnectingIterations);
                    if (iterations > stallThreshold)
                    {
                        // Timeout: iterations × health check interval (e.g., 10 × 30s = 5 minutes)
                        _logger.LogError(
                            "Reconnection stalled after {Iterations} iterations (~{Timeout}s). Forcing reset.",
                            iterations,
                            (int)(iterations * healthCheckInterval.TotalSeconds));

                        // Force reset reconnection flag to allow recovery
                        Interlocked.Exchange(ref _isReconnecting, 0);
                        Interlocked.Exchange(ref _reconnectingIterations, 0);

                        // Reset circuit breaker to allow immediate retry
                        _circuitBreaker?.Reset();
                    }
                }

                if (!_client.IsConnected)
                {
                    if (Interlocked.Exchange(ref _isReconnecting, 1) == 1)
                    {
                        continue; // Already reconnecting
                    }

                    try
                    {
                        var reconnectDelay = _configuration.ReconnectDelay;
                        while (!cancellationToken.IsCancellationRequested)
                        {
                            // Check circuit breaker
                            if (_circuitBreaker is not null && !_circuitBreaker.ShouldAttempt())
                            {
                                var cooldownRemaining = _circuitBreaker.GetCooldownRemaining();
                                _logger.LogWarning(
                                    "Circuit breaker open after {TripCount} trips. Pausing reconnection attempts for {Cooldown}s.",
                                    _circuitBreaker.TripCount,
                                    (int)cooldownRemaining.TotalSeconds);

                                // Wait for cooldown period (or until cancellation)
                                await Task.Delay(cooldownRemaining, cancellationToken).ConfigureAwait(false);
                                continue;
                            }

                            try
                            {
                                _logger.LogInformation("Attempting to reconnect to MQTT broker in {Delay}...", reconnectDelay);
                                await Task.Delay(reconnectDelay, cancellationToken).ConfigureAwait(false);

                                var options = _optionsBuilder();
                                await _client.ConnectAsync(options, cancellationToken).ConfigureAwait(false);

                                _logger.LogInformation("Reconnected to MQTT broker successfully.");
                                await _onReconnected(cancellationToken).ConfigureAwait(false);

                                // Success - close circuit breaker and reset counters
                                _circuitBreaker?.RecordSuccess();
                                Interlocked.Exchange(ref _reconnectingIterations, 0);
                                break;
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                            {
                                _logger.LogInformation("Reconnection cancelled due to shutdown.");
                                break;
                            }
                            catch (Exception ex)
                            {
                                var isPermanent = MqttExceptionClassifier.IsPermanentFailure(ex);
                                var description = MqttExceptionClassifier.GetFailureDescription(ex);

                                if (isPermanent)
                                {
                                    _logger.LogError(ex,
                                        "Permanent connection failure detected: {Description}. " +
                                        "Reconnection will be retried, but this likely requires configuration changes.",
                                        description);
                                }
                                else
                                {
                                    _logger.LogError(ex,
                                        "Failed to reconnect to MQTT broker: {Description}",
                                        description);
                                }

                                // Record failure in circuit breaker
                                if (_circuitBreaker is not null && _circuitBreaker.RecordFailure())
                                {
                                    _logger.LogWarning(
                                        "Circuit breaker tripped after {Threshold} consecutive failures. " +
                                        "Pausing reconnection attempts for {Cooldown}s.",
                                        _configuration.CircuitBreakerFailureThreshold,
                                        (int)_configuration.CircuitBreakerCooldown.TotalSeconds);
                                }

                                // Exponential backoff with jitter
                                var jitter = Random.Shared.NextDouble() * 0.1 + 0.95; // 0.95 to 1.05
                                reconnectDelay = TimeSpan.FromMilliseconds(
                                    Math.Min(reconnectDelay.TotalMilliseconds * 2 * jitter, maxDelay.TotalMilliseconds));
                            }
                        }
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _isReconnecting, 0);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("Connection monitoring cancelled due to shutdown.");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in connection monitoring.");
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return ValueTask.CompletedTask;
        }

        _reconnectSignal.Dispose();
        return ValueTask.CompletedTask;
    }
}
