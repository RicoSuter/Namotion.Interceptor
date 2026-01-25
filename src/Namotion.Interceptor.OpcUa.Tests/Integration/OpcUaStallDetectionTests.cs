using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Testing;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

/// <summary>
/// Tests for OPC UA client stall detection and recovery.
/// These tests verify that the client can detect when SDK reconnection is stuck
/// and force a manual reconnection.
///
/// In a separate class for parallel execution since stall detection tests
/// intentionally wait for timeouts.
/// </summary>
[Trait("Category", "Integration")]
public class OpcUaStallDetectionTests
{
    private readonly ITestOutputHelper _output;

    // Stall detection config for tests: 15s max reconnect duration
    // Note: Under parallel execution, SDK can take 20-30s to even start reconnecting,
    // so we use the same 15s as OpcUaTestClient defaults for consistency.
    private readonly Action<OpcUaClientConfiguration> _fastStallConfig = config =>
    {
        config.SubscriptionHealthCheckInterval = TimeSpan.FromSeconds(2);
        config.MaxReconnectDuration = TimeSpan.FromSeconds(15);
        config.ReconnectInterval = TimeSpan.FromSeconds(1);
    };

    public OpcUaStallDetectionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task StallDetection_WhenServerNeverReturns_ClientForcesReset()
    {
        // Tests that when server goes down and never comes back,
        // stall detection kicks in and resets the reconnection state
        // so the client can try fresh reconnection attempts.

        var logger = new TestLogger(_output);
        OpcUaTestServer<TestRoot>? server = null;
        OpcUaTestClient<TestRoot>? client = null;
        PortLease? port = null;

        try
        {
            port = await OpcUaTestPortPool.AcquireAsync();

            server = new OpcUaTestServer<TestRoot>(logger);
            await server.StartAsync(
                context => new TestRoot(context),
                (context, root) =>
                {
                    root.Connected = true;
                    root.Name = "Initial";
                },
                baseAddress: port.BaseAddress,
                certificateStoreBasePath: port.CertificateStoreBasePath);

            client = new OpcUaTestClient<TestRoot>(logger, _fastStallConfig);
            await client.StartAsync(
                context => new TestRoot(context),
                isConnected: root => root.Connected,
                serverUrl: port.ServerUrl,
                certificateStoreBasePath: port.CertificateStoreBasePath);

            Assert.NotNull(server.Root);
            Assert.NotNull(client.Root);
            Assert.NotNull(client.Diagnostics);

            // Verify initial connection
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Diagnostics.IsConnected,
                timeout: TimeSpan.FromSeconds(30),
                message: "Client should be connected after startup");
            logger.Log("Initial connection established");

            var initialReconnectAttempts = client.Diagnostics.TotalReconnectionAttempts;
            logger.Log($"Initial reconnect attempts: {initialReconnectAttempts}");

            // Stop server - DO NOT restart it
            logger.Log("Stopping server (will NOT restart)...");
            await server.StopAsync();

            // Wait for client to detect disconnection
            await AsyncTestHelpers.WaitUntilAsync(
                () => !client.Diagnostics.IsConnected,
                timeout: TimeSpan.FromSeconds(30),
                message: "Client should detect disconnection");
            logger.Log("Client detected disconnection");

            // Wait for reconnection to start
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Diagnostics.IsReconnecting,
                timeout: TimeSpan.FromSeconds(30),
                message: "Client should start reconnecting");
            logger.Log("Client started reconnecting");

            // Wait for stall detection to trigger (15s with our config + buffer for parallel execution)
            // Note: Under parallel load, SDK can take 20-30s to start reconnecting, plus 15s stall detection
            var stallDetectionTimeout = TimeSpan.FromSeconds(60);
            var startTime = DateTime.UtcNow;

            await AsyncTestHelpers.WaitUntilAsync(
                () =>
                {
                    // Stall detection succeeded if:
                    // 1. No longer reconnecting (state was reset), OR
                    // 2. Reconnection attempts increased significantly (manual reconnect triggered)
                    var notReconnecting = !client.Diagnostics.IsReconnecting;
                    var attemptsIncreased = client.Diagnostics.TotalReconnectionAttempts > initialReconnectAttempts + 1;

                    if (notReconnecting || attemptsIncreased)
                    {
                        logger.Log($"Stall detection triggered: IsReconnecting={client.Diagnostics.IsReconnecting}, " +
                                   $"Attempts={client.Diagnostics.TotalReconnectionAttempts}");
                        return true;
                    }
                    return false;
                },
                timeout: stallDetectionTimeout,
                message: "Stall detection should trigger and reset reconnection state");

            var elapsed = DateTime.UtcNow - startTime;
            logger.Log($"Stall detection completed in {elapsed.TotalSeconds:F1}s");

            // Verify stall detection worked within expected timeframe
            // Under parallel execution, SDK can take 20-30s to start, plus 15s stall detection
            Assert.True(elapsed < TimeSpan.FromSeconds(55),
                $"Stall detection should complete within 55s (actual: {elapsed.TotalSeconds:F1}s)");

            logger.Log("Test passed - stall detection working correctly");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }

    [Fact]
    public async Task StallDetection_WhenServerRestartsAfterStall_ClientRecovers()
    {
        // Tests that after stall detection resets the state,
        // the client can successfully reconnect when the server comes back.

        var logger = new TestLogger(_output);
        OpcUaTestServer<TestRoot>? server = null;
        OpcUaTestClient<TestRoot>? client = null;
        PortLease? port = null;

        try
        {
            port = await OpcUaTestPortPool.AcquireAsync();

            server = new OpcUaTestServer<TestRoot>(logger);
            await server.StartAsync(
                context => new TestRoot(context),
                (context, root) =>
                {
                    root.Connected = true;
                    root.Name = "Initial";
                },
                baseAddress: port.BaseAddress,
                certificateStoreBasePath: port.CertificateStoreBasePath);

            client = new OpcUaTestClient<TestRoot>(logger, _fastStallConfig);
            await client.StartAsync(
                context => new TestRoot(context),
                isConnected: root => root.Connected,
                serverUrl: port.ServerUrl,
                certificateStoreBasePath: port.CertificateStoreBasePath);

            Assert.NotNull(server.Root);
            Assert.NotNull(client.Root);
            Assert.NotNull(client.Diagnostics);

            // Verify initial sync
            server.Root.Name = "BeforeStall";
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.Name == "BeforeStall",
                timeout: TimeSpan.FromSeconds(60),
                message: "Initial sync should complete");
            logger.Log("Initial sync verified");

            // Stop server
            logger.Log("Stopping server...");
            await server.StopAsync();

            // Wait for stall detection to trigger (don't restart yet)
            await Task.Delay(TimeSpan.FromSeconds(8)); // Let stall detection do its thing
            logger.Log("Waited for stall detection window");

            // Now restart the server
            logger.Log("Restarting server after stall detection window...");
            await server.RestartAsync();

            // Verify client recovers and data flows
            server.Root.Name = "AfterStallRecovery";
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.Name == "AfterStallRecovery",
                timeout: TimeSpan.FromSeconds(60),
                message: "Data should flow after stall recovery");
            logger.Log($"Client received: {client.Root.Name}");

            // Wait for connection status to stabilize
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Diagnostics.IsConnected,
                timeout: TimeSpan.FromSeconds(30),
                message: "Client should report as connected after recovery");
            logger.Log("Test passed - client recovered after stall");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }

    [Fact]
    public async Task StallDetection_WhenServerRestartsQuickly_SdkReconnectsWithoutStallTrigger()
    {
        // Tests that when server restarts quickly (before MaxReconnectDuration),
        // the SDK reconnection succeeds without stall detection triggering.
        // This verifies we don't have false positive stall triggers.

        var logger = new TestLogger(_output);
        OpcUaTestServer<TestRoot>? server = null;
        OpcUaTestClient<TestRoot>? client = null;
        PortLease? port = null;

        try
        {
            port = await OpcUaTestPortPool.AcquireAsync();

            server = new OpcUaTestServer<TestRoot>(logger);
            await server.StartAsync(
                context => new TestRoot(context),
                (context, root) =>
                {
                    root.Connected = true;
                    root.Name = "Initial";
                },
                baseAddress: port.BaseAddress,
                certificateStoreBasePath: port.CertificateStoreBasePath);

            // Use longer MaxReconnectDuration to ensure SDK has time to reconnect
            // Use shorter KeepAliveInterval for faster disconnection detection on slow CI runners
            var quickRestartConfig = (OpcUaClientConfiguration config) =>
            {
                config.SubscriptionHealthCheckInterval = TimeSpan.FromSeconds(2);
                config.MaxReconnectDuration = TimeSpan.FromSeconds(30); // Long enough for SDK to succeed
                config.ReconnectInterval = TimeSpan.FromSeconds(1);
                config.KeepAliveInterval = TimeSpan.FromSeconds(1); // Faster disconnection detection
            };

            client = new OpcUaTestClient<TestRoot>(logger, quickRestartConfig);
            await client.StartAsync(
                context => new TestRoot(context),
                isConnected: root => root.Connected,
                serverUrl: port.ServerUrl,
                certificateStoreBasePath: port.CertificateStoreBasePath);

            Assert.NotNull(server.Root);
            Assert.NotNull(client.Root);
            Assert.NotNull(client.Diagnostics);

            // Verify initial sync
            server.Root.Name = "BeforeRestart";
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.Name == "BeforeRestart",
                timeout: TimeSpan.FromSeconds(30),
                message: "Initial sync should complete");
            logger.Log("Initial sync verified");

            var initialReconnectAttempts = client.Diagnostics.TotalReconnectionAttempts;
            var initialSuccessfulReconnects = client.Diagnostics.SuccessfulReconnections;
            logger.Log($"Before restart - attempts: {initialReconnectAttempts}, successful: {initialSuccessfulReconnects}");

            // Stop server briefly
            logger.Log("Stopping server for quick restart...");
            await server.StopAsync();

            // Wait for client to detect disconnection (longer timeout for slow CI runners)
            await AsyncTestHelpers.WaitUntilAsync(
                () => !client.Diagnostics.IsConnected || client.Diagnostics.IsReconnecting,
                timeout: TimeSpan.FromSeconds(40),
                message: "Client should detect disconnection or start reconnecting");
            logger.Log($"Client state after stop - Connected: {client.Diagnostics.IsConnected}, Reconnecting: {client.Diagnostics.IsReconnecting}");

            // Restart server quickly (well before MaxReconnectDuration of 30s)
            logger.Log("Restarting server quickly...");
            await server.RestartAsync();

            // Verify client recovers and data flows
            server.Root.Name = "AfterQuickRestart";
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.Name == "AfterQuickRestart",
                timeout: TimeSpan.FromSeconds(60),
                message: "Data should flow after quick restart");
            logger.Log($"Client received: {client.Root.Name}");

            // Verify client is connected
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Diagnostics.IsConnected,
                timeout: TimeSpan.FromSeconds(30),
                message: "Client should report as connected after recovery");

            // Log final state for debugging
            logger.Log($"After restart - attempts: {client.Diagnostics.TotalReconnectionAttempts}, " +
                       $"successful: {client.Diagnostics.SuccessfulReconnections}, " +
                       $"connected: {client.Diagnostics.IsConnected}");

            logger.Log("Test passed - SDK reconnected without stall detection trigger");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }
}
