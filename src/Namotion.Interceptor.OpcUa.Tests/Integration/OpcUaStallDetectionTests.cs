using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Testing;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

/// <summary>
/// Tests for outages that outlast a reconnection cycle (<c>MaxReconnectDuration</c>): the client
/// keeps retrying in new cycles and recovers once the server is back.
/// </summary>
[Trait("Category", "Integration")]
public class OpcUaStallDetectionTests
{
    private readonly ITestOutputHelper _output;

    // A reconnection cycle of 15s, the same as the OpcUaTestClient defaults.
    // SessionTimeout determines when connection is considered lost (min 10s due to server MinSessionTimeout)
    // KeepAliveInterval is set to 1s for fast disconnection detection.
    private readonly Action<OpcUaClientConfiguration> _fastStallConfig = config =>
    {
        config.SubscriptionHealthCheckInterval = TimeSpan.FromSeconds(2);
        config.MaxReconnectDuration = TimeSpan.FromSeconds(15);
        config.ReconnectInterval = TimeSpan.FromSeconds(1);
        config.SessionTimeout = TimeSpan.FromSeconds(10); // Minimum allowed by server
        config.KeepAliveInterval = TimeSpan.FromSeconds(1); // Fast disconnection detection
    };

    public OpcUaStallDetectionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task StallDetection_WhenServerNeverReturns_ClientKeepsRetrying()
    {
        // When the server goes down and never comes back, each reconnection cycle that exhausts
        // MaxReconnectDuration counts as a failed attempt and the managed session starts the next
        // one, so the client keeps retrying instead of getting stuck.

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
            Assert.NotNull(client.Source!.Diagnostics);

            // Verify initial connection
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Source!.Diagnostics.IsOperational == true,
                timeout: TimeSpan.FromSeconds(120),
                message: "Client should be connected after startup");
            logger.Log("Initial connection established");

            var initialReconnectAttempts = client.Source!.Diagnostics.Reconnects.TotalAttempts;
            logger.Log($"Initial reconnect attempts: {initialReconnectAttempts}");

            // Stop server - DO NOT restart it
            logger.Log("Stopping server (will NOT restart)...");
            await server.StopAsync();

            // Wait for client to detect disconnection (longer timeout for parallel test execution)
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Source!.Diagnostics.IsOperational == false,
                timeout: TimeSpan.FromSeconds(120),
                message: "Client should detect disconnection");
            logger.Log("Client detected disconnection");

            // Wait for reconnection to start.
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Source!.Diagnostics.Reconnects.TotalAttempts > initialReconnectAttempts,
                timeout: TimeSpan.FromSeconds(120),
                message: "Client should start reconnecting");
            logger.Log($"Client started reconnecting (attempts: {client.Source!.Diagnostics.Reconnects.TotalAttempts})");

            // Each exhausted cycle (MaxReconnectDuration, 15s here) starts a new attempt, so the
            // counter keeps climbing while the server stays away.
            var recoveryTimeout = TimeSpan.FromSeconds(90);
            var startTime = DateTime.UtcNow;

            await AsyncTestHelpers.WaitUntilAsync(
                () =>
                {
                    var attempts = client.Source!.Diagnostics.Reconnects.TotalAttempts;
                    if (attempts > initialReconnectAttempts + 2)
                    {
                        logger.Log($"Recovery verified: IsReconnecting={client.Source!.Diagnostics.IsReconnecting}, " +
                                   $"Attempts={attempts}");
                        return true;
                    }
                    return false;
                },
                timeout: recoveryTimeout,
                message: "Client should keep retrying reconnection");

            var elapsed = DateTime.UtcNow - startTime;
            logger.Log($"Recovery behavior confirmed in {elapsed.TotalSeconds:F1}s");

            // Verify recovery happened within expected timeframe
            Assert.True(elapsed < TimeSpan.FromSeconds(85),
                $"Recovery should be confirmed within 85s (actual: {elapsed.TotalSeconds:F1}s)");

            logger.Log("Test passed - client correctly handles server-never-returns scenario");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }

    [Fact]
    public async Task StallDetection_WhenServerRestartsAfterAnExhaustedCycle_ClientRecovers()
    {
        // After a whole reconnection cycle failed, a later cycle reconnects once the server is back,
        // and values keep arriving through the subscription afterwards.

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
            Assert.NotNull(client.Source!.Diagnostics);

            // Several notification messages before the outage, so the subscription's sequence numbers
            // are well past the ones a subscription created again on the server starts with.
            for (var i = 0; i < 20; i++)
            {
                var name = $"BeforeOutage{i}";
                server.Root.Name = name;
                await AsyncTestHelpers.WaitUntilAsync(
                    () => client.Root.Name == name,
                    timeout: TimeSpan.FromSeconds(120),
                    message: "Initial sync should complete");
            }
            logger.Log("Initial sync verified");

            var failedBeforeOutage = client.Source!.Diagnostics.Reconnects.TotalFailed;

            // Stop server
            logger.Log("Stopping server...");
            await server.StopAsync();

            // Stay down until a whole reconnection cycle was exhausted.
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Source!.Diagnostics.Reconnects.TotalFailed > failedBeforeOutage,
                timeout: TimeSpan.FromSeconds(120),
                message: "A reconnection cycle should be exhausted while the server stays down");
            Assert.False(client.Source!.Diagnostics.IsOperational);
            logger.Log("A reconnection cycle was exhausted");

            // Now restart the server
            logger.Log("Restarting server after the exhausted cycle...");
            await server.RestartAsync();

            // Verify client recovers and data flows
            server.Root.Name = "AfterStallRecovery";
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.Name == "AfterStallRecovery",
                timeout: TimeSpan.FromSeconds(120),
                message: "Data should flow after stall recovery");
            logger.Log($"Client received: {client.Root.Name}");

            // Wait for connection status to stabilize
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Source!.Diagnostics.IsOperational == true,
                timeout: TimeSpan.FromSeconds(120),
                message: "Client should report as connected after recovery");

            // Several changes one after the other: the single full state read after the recovery can
            // deliver at most one of them, the others have to arrive through the subscription, which
            // proves that notifications of the restarted numbering are applied.
            for (var i = 0; i < 3; i++)
            {
                var name = $"AfterRecovery{i}";
                server.Root.Name = name;
                await AsyncTestHelpers.WaitUntilAsync(
                    () => client.Root.Name == name,
                    timeout: TimeSpan.FromSeconds(120),
                    message: "A change after the recovery should arrive through the subscription");
            }

            logger.Log("Test passed - client recovered after an exhausted reconnection cycle");
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
                (_, root) =>
                {
                    root.Connected = true;
                    root.Name = "Initial";
                },
                baseAddress: port.BaseAddress,
                certificateStoreBasePath: port.CertificateStoreBasePath);

            // Use longer MaxReconnectDuration to ensure SDK has time to reconnect
            // Use shorter SessionTimeout and KeepAliveInterval for faster disconnection detection on slow CI runners
            void QuickRestartConfig(OpcUaClientConfiguration config)
            {
                config.SubscriptionHealthCheckInterval = TimeSpan.FromSeconds(2);
                config.MaxReconnectDuration = TimeSpan.FromSeconds(60); // Long enough for SDK to succeed under CI load
                config.ReconnectInterval = TimeSpan.FromSeconds(1);
                config.SessionTimeout = TimeSpan.FromSeconds(10); // Minimum allowed by server for fast detection
                config.KeepAliveInterval = TimeSpan.FromSeconds(1); // Faster disconnection detection
            }

            client = new OpcUaTestClient<TestRoot>(logger, QuickRestartConfig);
            await client.StartAsync(
                context => new TestRoot(context),
                isConnected: root => root.Connected,
                serverUrl: port.ServerUrl,
                certificateStoreBasePath: port.CertificateStoreBasePath);

            Assert.NotNull(server.Root);
            Assert.NotNull(client.Root);
            Assert.NotNull(client.Source!.Diagnostics);

            // Verify initial sync
            server.Root.Name = "BeforeRestart";
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.Name == "BeforeRestart",
                timeout: TimeSpan.FromSeconds(120),
                message: "Initial sync should complete");
            logger.Log("Initial sync verified");

            var initialReconnectAttempts = client.Source!.Diagnostics.Reconnects.TotalAttempts;
            var initialSuccessfulReconnects = client.Source!.Diagnostics.Reconnects.TotalSucceeded;
            logger.Log($"Before restart - attempts: {initialReconnectAttempts}, successful: {initialSuccessfulReconnects}");

            // Stop server briefly
            logger.Log("Stopping server for quick restart...");
            await server.StopAsync();

            // Wait for client to detect disconnection (longer timeout for slow CI runners)
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Source!.Diagnostics.IsOperational == false || client.Source!.Diagnostics.IsReconnecting,
                timeout: TimeSpan.FromSeconds(120),
                message: "Client should detect disconnection or start reconnecting");
            logger.Log($"Client state after stop - Connected: {client.Source!.Diagnostics.IsOperational}, Reconnecting: {client.Source!.Diagnostics.IsReconnecting}");

            // Restart server quickly (well before MaxReconnectDuration of 30s)
            logger.Log("Restarting server quickly...");
            await server.RestartAsync();

            // Verify client recovers and data flows
            server.Root.Name = "AfterQuickRestart";
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.Name == "AfterQuickRestart",
                timeout: TimeSpan.FromSeconds(120),
                message: "Data should flow after quick restart");
            logger.Log($"Client received: {client.Root.Name}");

            // Verify client is connected
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Source!.Diagnostics.IsOperational == true,
                timeout: TimeSpan.FromSeconds(120),
                message: "Client should report as connected after recovery");

            // Log final state for debugging
            logger.Log($"After restart - attempts: {client.Source!.Diagnostics.Reconnects.TotalAttempts}, " +
                       $"successful: {client.Source!.Diagnostics.Reconnects.TotalSucceeded}, " +
                       $"connected: {client.Source!.Diagnostics.IsOperational}");

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
