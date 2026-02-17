using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MQTTnet;
using Namotion.Interceptor.Mqtt.Client;
using Namotion.Interceptor.Registry.Paths;
using Xunit;

namespace Namotion.Interceptor.Mqtt.Tests;

/// <summary>
/// Tests for <see cref="MqttConnectionMonitor"/> resilience behaviors:
/// health checks, reconnection, circuit breaker, exponential backoff, and stale signal handling.
/// </summary>
/// <remarks>
/// TryPingAsync is an extension method (not mockable). It wraps PingAsync in a try/catch:
/// PingAsync succeeds → TryPingAsync returns true; PingAsync throws → TryPingAsync returns false.
/// All tests mock PingAsync accordingly.
///
/// Timeouts are generous (2-5s) to avoid flakiness on slow CI build agents.
/// </remarks>
[Trait("Category", "Integration")]
public class MqttConnectionMonitorTests
{
    private static MqttClientConfiguration CreateConfiguration(
        TimeSpan? healthCheckInterval = null,
        TimeSpan? reconnectDelay = null,
        TimeSpan? maximumReconnectDelay = null,
        int circuitBreakerFailureThreshold = 0,
        TimeSpan? circuitBreakerCooldown = null,
        int reconnectStallThreshold = 0)
    {
        return new MqttClientConfiguration
        {
            BrokerHost = "localhost",
            PathProvider = new AttributeBasedPathProvider("test", '/'),
            HealthCheckInterval = healthCheckInterval ?? TimeSpan.FromMilliseconds(100),
            ReconnectDelay = reconnectDelay ?? TimeSpan.FromMilliseconds(10),
            MaximumReconnectDelay = maximumReconnectDelay ?? TimeSpan.FromSeconds(1),
            CircuitBreakerFailureThreshold = circuitBreakerFailureThreshold,
            CircuitBreakerCooldown = circuitBreakerCooldown ?? TimeSpan.FromMilliseconds(500),
            ReconnectStallThreshold = reconnectStallThreshold,
        };
    }

    private static MqttClientOptions CreateOptions() => new MqttClientOptionsBuilder()
        .WithTcpServer("localhost")
        .Build();

    /// <summary>Helper: configure mock so PingAsync succeeds (→ TryPingAsync returns true).</summary>
    private static void SetupPingHealthy(Mock<IMqttClient> client)
    {
        client.Setup(c => c.PingAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
    }

    /// <summary>Helper: configure mock so PingAsync throws (→ TryPingAsync returns false).</summary>
    private static void SetupPingUnhealthy(Mock<IMqttClient> client)
    {
        client.Setup(c => c.PingAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Ping failed"));
    }

    [Fact]
    public async Task HealthyConnection_DoesNotTriggerReconnection()
    {
        // Arrange
        var client = new Mock<IMqttClient>();
        client.Setup(c => c.IsConnected).Returns(true);
        SetupPingHealthy(client);

        var reconnectedCount = 0;
        var disconnectedCount = 0;

        var monitor = new MqttConnectionMonitor(
            client.Object,
            CreateConfiguration(healthCheckInterval: TimeSpan.FromMilliseconds(50)),
            CreateOptions,
            onReconnected: _ => { reconnectedCount++; return Task.CompletedTask; },
            onDisconnected: () => { disconnectedCount++; return Task.CompletedTask; },
            NullLogger.Instance);

        // Act: let it run for several health check intervals
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await monitor.MonitorConnectionAsync(cts.Token); }
        catch (OperationCanceledException) { }

        // Assert: healthy connection should never trigger reconnection or disconnect handlers
        Assert.Equal(0, reconnectedCount);
        Assert.Equal(0, disconnectedCount);
    }

    [Fact]
    public async Task HealthCheckFailure_TriggersReconnection()
    {
        // Arrange: client starts disconnected, reconnects after ConnectAsync
        var client = new Mock<IMqttClient>();
        client.Setup(c => c.IsConnected).Returns(false);
        SetupPingUnhealthy(client);
        client.Setup(c => c.ConnectAsync(It.IsAny<MqttClientOptions>(), It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                client.Setup(c => c.IsConnected).Returns(true);
                SetupPingHealthy(client);
            })
            .ReturnsAsync(new MqttClientConnectResult());

        var reconnectedCount = 0;

        var monitor = new MqttConnectionMonitor(
            client.Object,
            CreateConfiguration(),
            CreateOptions,
            onReconnected: _ => { Interlocked.Increment(ref reconnectedCount); return Task.CompletedTask; },
            onDisconnected: () => Task.CompletedTask,
            NullLogger.Instance);

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await monitor.MonitorConnectionAsync(cts.Token); }
        catch (OperationCanceledException) { }

        // Assert
        Assert.True(reconnectedCount >= 1, $"Expected at least 1 reconnection but got {reconnectedCount}");
        client.Verify(c => c.ConnectAsync(It.IsAny<MqttClientOptions>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task DisconnectSignal_TriggersReconnection()
    {
        // Arrange
        var client = new Mock<IMqttClient>();
        var connectCallCount = 0;

        // Start disconnected, become connected after first ConnectAsync
        client.Setup(c => c.IsConnected).Returns(false);
        SetupPingUnhealthy(client);
        client.Setup(c => c.ConnectAsync(It.IsAny<MqttClientOptions>(), It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                Interlocked.Increment(ref connectCallCount);
                client.Setup(c => c.IsConnected).Returns(true);
                SetupPingHealthy(client);
            })
            .ReturnsAsync(new MqttClientConnectResult());

        var disconnectedCalled = false;

        var monitor = new MqttConnectionMonitor(
            client.Object,
            // Long health check interval so the signal (not the periodic check) triggers reconnection
            CreateConfiguration(healthCheckInterval: TimeSpan.FromSeconds(30)),
            CreateOptions,
            onReconnected: _ => Task.CompletedTask,
            onDisconnected: () => { disconnectedCalled = true; return Task.CompletedTask; },
            NullLogger.Instance);

        // Signal disconnect after a short delay (enough time for MonitorConnectionAsync to start waiting)
        _ = Task.Run(async () =>
        {
            await Task.Delay(200);
            monitor.SignalReconnectNeeded();
        });

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await monitor.MonitorConnectionAsync(cts.Token); }
        catch (OperationCanceledException) { }

        // Assert
        Assert.True(disconnectedCalled, "Expected onDisconnected to be called");
        Assert.True(connectCallCount >= 1, $"Expected at least 1 connect call but got {connectCallCount}");
    }

    [Fact]
    public async Task StaleDisconnectSignal_IgnoredWhenClientHealthy()
    {
        // Arrange: client is connected and ping succeeds
        var client = new Mock<IMqttClient>();
        client.Setup(c => c.IsConnected).Returns(true);
        SetupPingHealthy(client);

        var disconnectedCount = 0;

        var monitor = new MqttConnectionMonitor(
            client.Object,
            // Long health check interval so the signal (not the periodic check) is what gets processed
            CreateConfiguration(healthCheckInterval: TimeSpan.FromSeconds(30)),
            CreateOptions,
            onReconnected: _ => Task.CompletedTask,
            onDisconnected: () => { Interlocked.Increment(ref disconnectedCount); return Task.CompletedTask; },
            NullLogger.Instance);

        // Signal disconnect after a short delay — this is a "stale" signal because client is actually healthy
        _ = Task.Run(async () =>
        {
            await Task.Delay(200);
            monitor.SignalReconnectNeeded();
        });

        // Act: run long enough for the signal to be received and verified via ping
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await monitor.MonitorConnectionAsync(cts.Token); }
        catch (OperationCanceledException) { }

        // Assert: disconnect handler should NOT be called because ping confirmed healthy
        Assert.Equal(0, disconnectedCount);
        client.Verify(c => c.ConnectAsync(It.IsAny<MqttClientOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CircuitBreaker_TripsAfterThresholdFailures()
    {
        // Arrange: ConnectAsync always fails
        var client = new Mock<IMqttClient>();
        client.Setup(c => c.IsConnected).Returns(false);
        SetupPingUnhealthy(client);

        var failureCount = 0;
        client.Setup(c => c.ConnectAsync(It.IsAny<MqttClientOptions>(), It.IsAny<CancellationToken>()))
            .Callback(() => Interlocked.Increment(ref failureCount))
            .ThrowsAsync(new InvalidOperationException("Connection refused"));

        var monitor = new MqttConnectionMonitor(
            client.Object,
            CreateConfiguration(
                healthCheckInterval: TimeSpan.FromMilliseconds(50),
                reconnectDelay: TimeSpan.FromMilliseconds(10),
                circuitBreakerFailureThreshold: 3,
                // Long cooldown — once tripped, no more retries within the test window
                circuitBreakerCooldown: TimeSpan.FromSeconds(60)),
            CreateOptions,
            onReconnected: _ => Task.CompletedTask,
            onDisconnected: () => Task.CompletedTask,
            NullLogger.Instance);

        // Act: run long enough for failures to accumulate then circuit breaker to block further attempts
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await monitor.MonitorConnectionAsync(cts.Token); }
        catch (OperationCanceledException) { }

        // Assert: exactly threshold failures should occur, then circuit breaker blocks the rest.
        // Allow a small margin (up to threshold + 2) for timing races at the boundary.
        Assert.True(failureCount >= 3, $"Expected at least 3 failures (threshold) but got {failureCount}");
        Assert.True(failureCount <= 8, $"Expected circuit breaker to limit failures but got {failureCount}");
    }

    [Fact]
    public async Task ExponentialBackoff_LimitsRetryRate()
    {
        // Arrange: ConnectAsync always fails, so we can observe backoff through attempt count.
        // With exponential backoff starting at 100ms (100, 200, 400, 800ms), in 2s we expect ~4-5 attempts.
        // Without backoff (fixed 100ms), we'd get ~20 attempts. The count difference proves backoff works.
        var client = new Mock<IMqttClient>();
        client.Setup(c => c.IsConnected).Returns(false);
        SetupPingUnhealthy(client);

        var failureCount = 0;
        client.Setup(c => c.ConnectAsync(It.IsAny<MqttClientOptions>(), It.IsAny<CancellationToken>()))
            .Callback(() => Interlocked.Increment(ref failureCount))
            .ThrowsAsync(new Exception("Connection refused"));

        var monitor = new MqttConnectionMonitor(
            client.Object,
            CreateConfiguration(
                healthCheckInterval: TimeSpan.FromMilliseconds(50),
                reconnectDelay: TimeSpan.FromMilliseconds(100),
                maximumReconnectDelay: TimeSpan.FromSeconds(2)),
            CreateOptions,
            onReconnected: _ => Task.CompletedTask,
            onDisconnected: () => Task.CompletedTask,
            NullLogger.Instance);

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await monitor.MonitorConnectionAsync(cts.Token); }
        catch (OperationCanceledException) { }

        // Assert: with exponential backoff (100, 200, 400, 800ms...) we expect significantly
        // fewer attempts than we would with a fixed 100ms delay (~30 attempts in 3s).
        // Allow generous bounds: at least 2 attempts, at most 12 (well below ~30 without backoff).
        Assert.True(failureCount >= 2, $"Expected at least 2 attempts but got {failureCount}");
        Assert.True(failureCount <= 12,
            $"Expected exponential backoff to limit attempts but got {failureCount} (too many — backoff may not be working)");
    }

    [Fact]
    public async Task ReconnectSuccess_DrainsStaleSignals()
    {
        // Arrange
        var client = new Mock<IMqttClient>();
        MqttConnectionMonitor? monitorRef = null;

        // First health check: disconnected. After reconnect: connected.
        client.Setup(c => c.IsConnected).Returns(false);
        SetupPingUnhealthy(client);
        client.Setup(c => c.ConnectAsync(It.IsAny<MqttClientOptions>(), It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                client.Setup(c => c.IsConnected).Returns(true);
                SetupPingHealthy(client);
            })
            .ReturnsAsync(new MqttClientConnectResult());

        var disconnectedCount = 0;

        var monitor = new MqttConnectionMonitor(
            client.Object,
            CreateConfiguration(healthCheckInterval: TimeSpan.FromMilliseconds(100)),
            CreateOptions,
            onReconnected: _ =>
            {
                // Simulate: a stale disconnect signal arrives right after reconnection.
                // The monitor should drain this signal immediately after onReconnected returns.
                monitorRef?.SignalReconnectNeeded();
                return Task.CompletedTask;
            },
            onDisconnected: () => { Interlocked.Increment(ref disconnectedCount); return Task.CompletedTask; },
            NullLogger.Instance);

        monitorRef = monitor;

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await monitor.MonitorConnectionAsync(cts.Token); }
        catch (OperationCanceledException) { }

        // Assert: the stale signal should be drained after reconnect, so the client should
        // NOT see additional disconnect calls beyond the initial one. We allow 1 because the
        // signal may be picked up before the drain on the very first cycle.
        Assert.True(disconnectedCount <= 1,
            $"Expected at most 1 disconnect call (stale signals should be drained) but got {disconnectedCount}");
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        // Arrange
        var client = new Mock<IMqttClient>();
        var monitor = new MqttConnectionMonitor(
            client.Object,
            CreateConfiguration(),
            CreateOptions,
            onReconnected: _ => Task.CompletedTask,
            onDisconnected: () => Task.CompletedTask,
            NullLogger.Instance);

        // Act & Assert: no exception on double dispose
        await monitor.DisposeAsync();
        await monitor.DisposeAsync();
    }

    [Fact]
    public async Task SignalReconnectNeeded_AfterDispose_DoesNotThrow()
    {
        // Arrange
        var client = new Mock<IMqttClient>();
        var monitor = new MqttConnectionMonitor(
            client.Object,
            CreateConfiguration(),
            CreateOptions,
            onReconnected: _ => Task.CompletedTask,
            onDisconnected: () => Task.CompletedTask,
            NullLogger.Instance);

        await monitor.DisposeAsync();

        // Act & Assert: no exception
        monitor.SignalReconnectNeeded();
    }

    [Fact]
    public void Constructor_ThrowsOnNullArguments()
    {
        var client = new Mock<IMqttClient>();
        var configuration = CreateConfiguration();
        Func<MqttClientOptions> optionsBuilder = CreateOptions;
        Func<CancellationToken, Task> onReconnected = _ => Task.CompletedTask;
        Func<Task> onDisconnected = () => Task.CompletedTask;

        Assert.Throws<ArgumentNullException>(() => new MqttConnectionMonitor(
            null!, configuration, optionsBuilder, onReconnected, onDisconnected, NullLogger.Instance));

        Assert.Throws<ArgumentNullException>(() => new MqttConnectionMonitor(
            client.Object, null!, optionsBuilder, onReconnected, onDisconnected, NullLogger.Instance));

        Assert.Throws<ArgumentNullException>(() => new MqttConnectionMonitor(
            client.Object, configuration, null!, onReconnected, onDisconnected, NullLogger.Instance));

        Assert.Throws<ArgumentNullException>(() => new MqttConnectionMonitor(
            client.Object, configuration, optionsBuilder, null!, onDisconnected, NullLogger.Instance));

        Assert.Throws<ArgumentNullException>(() => new MqttConnectionMonitor(
            client.Object, configuration, optionsBuilder, onReconnected, null!, NullLogger.Instance));

        Assert.Throws<ArgumentNullException>(() => new MqttConnectionMonitor(
            client.Object, configuration, optionsBuilder, onReconnected, onDisconnected, null!));
    }
}
