using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MQTTnet;

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
    private int _isReconnecting;
    private int _disposed;

    public MqttConnectionMonitor(
        IMqttClient client,
        MqttClientConfiguration configuration,
        ILogger logger,
        Func<MqttClientOptions> optionsBuilder,
        Func<CancellationToken, Task> onReconnected,
        Func<Task> onDisconnected)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _optionsBuilder = optionsBuilder ?? throw new ArgumentNullException(nameof(optionsBuilder));
        _onReconnected = onReconnected ?? throw new ArgumentNullException(nameof(onReconnected));
        _onDisconnected = onDisconnected ?? throw new ArgumentNullException(nameof(onDisconnected));
    }

    /// <summary>
    /// Signals that a reconnection is needed (called by DisconnectedAsync event handler).
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
    /// Monitors connection health and performs reconnection with exponential backoff.
    /// This is a blocking method that runs until cancellation is requested.
    /// Uses hybrid approach: waits for disconnect event OR periodic health check.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to stop monitoring.</param>
    public async Task MonitorConnectionAsync(CancellationToken cancellationToken)
    {
        var delay = _configuration.ReconnectDelay;
        var maxDelay = _configuration.MaxReconnectDelay;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Wait for either: disconnect event signal OR periodic health check timeout
                var signaled = await _reconnectSignal.WaitAsync(delay, cancellationToken).ConfigureAwait(false);

                if (signaled)
                {
                    _logger.LogWarning("MQTT disconnect event received.");
                    await _onDisconnected().ConfigureAwait(false);
                }
                else
                {
                    // Periodic health check - use TryPingAsync (recommended by MQTTnet)
                    var isHealthy = _client.IsConnected && await _client.TryPingAsync(cancellationToken).ConfigureAwait(false);
                    if (isHealthy)
                    {
                        continue; // Connection is healthy, keep waiting
                    }

                    _logger.LogWarning("MQTT health check failed.");
                }

                // At this point, we need to reconnect (either event fired or health check failed)
                var isConnected = false;

                if (!isConnected || !_client.IsConnected)
                {
                    if (Interlocked.Exchange(ref _isReconnecting, 1) == 1)
                    {
                        continue; // Already reconnecting
                    }

                    try
                    {
                        // Perform clean disconnect before reconnecting
                        if (_client.IsConnected)
                        {
                            try
                            {
                                await _client.DisconnectAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Error during clean disconnect before reconnect.");
                            }
                        }

                        var reconnectDelay = _configuration.ReconnectDelay;

                        while (!cancellationToken.IsCancellationRequested)
                        {
                            try
                            {
                                _logger.LogInformation("Attempting to reconnect to MQTT broker in {Delay}...", reconnectDelay);
                                await Task.Delay(reconnectDelay, cancellationToken).ConfigureAwait(false);

                                var options = _optionsBuilder();
                                await _client.ConnectAsync(options, cancellationToken).ConfigureAwait(false);

                                _logger.LogInformation("Reconnected to MQTT broker successfully.");
                                await _onReconnected(cancellationToken).ConfigureAwait(false);

                                delay = _configuration.ReconnectDelay;
                                break;
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                            {
                                _logger.LogInformation("Reconnection cancelled due to shutdown.");
                                break;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Failed to reconnect to MQTT broker.");
                              
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

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _reconnectSignal.Dispose();
        await ValueTask.CompletedTask;
    }
}
