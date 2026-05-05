using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MQTTnet;

namespace Namotion.Interceptor.Mqtt.Client;

/// <summary>
/// Owns the listen-time resources spawned by <see cref="MqttSubjectClientSource.StartListeningAsync"/>:
/// the connection-monitor task, its cancellation source, the connection monitor itself,
/// and the MQTT client connection. Disposed by the base loop on retry or shutdown.
/// </summary>
internal sealed class MqttConnectionLifetime : IAsyncDisposable
{
    private readonly MqttSubjectClientSource _source;
    private readonly IMqttClient _client;
    private readonly MqttConnectionMonitor _connectionMonitor;
    private readonly CancellationTokenSource _monitorCts;
    private readonly Task _monitorTask;
    private readonly ILogger _logger;
    private int _disposed;

    public MqttConnectionLifetime(
        MqttSubjectClientSource source,
        IMqttClient client,
        MqttConnectionMonitor connectionMonitor,
        CancellationTokenSource monitorCts,
        Task monitorTask,
        ILogger logger)
    {
        _source = source;
        _client = client;
        _connectionMonitor = connectionMonitor;
        _monitorCts = monitorCts;
        _monitorTask = monitorTask;
        _logger = logger;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        // Cancel and await the monitor task before tearing down the client.
        // This guarantees the spec's "loop only exists during a successful listen" property:
        // by the time DisposeAsync returns, the connection-monitor's reconnection loop is
        // not running, so it cannot race with subsequent listen iterations.
        try { await _monitorCts.CancelAsync().ConfigureAwait(false); } catch { /* ignore */ }
        try { await _monitorTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected */ }
        catch (Exception ex) { _logger.LogWarning(ex, "MQTT connection-monitor task threw during disposal."); }
        try { _monitorCts.Dispose(); } catch { /* ignore */ }

        // Dispose the connection monitor (releases its internal semaphore).
        try { await _connectionMonitor.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "MQTT connection monitor threw during disposal."); }

        // Detach event handlers so a still-connected client cannot fire callbacks
        // into a half-disposed source between this disposal and the next listen.
        _client.ApplicationMessageReceivedAsync -= _source.OnMessageReceivedAsync;
        _client.DisconnectedAsync -= _source.OnDisconnectedAsync;

        try
        {
            if (_client.IsConnected)
            {
                await _client.DisconnectAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disconnecting MQTT client during listen-lifetime disposal.");
        }

        try { _client.Dispose(); } catch { /* ignore */ }

        // Clear source-level fields so the next listen starts from a clean slate.
        _source.OnListenLifetimeDisposed();
    }
}
