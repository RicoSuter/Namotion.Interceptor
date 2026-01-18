using Namotion.Interceptor.Testing;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

[Collection("OPC UA Integration")]
[Trait("Category", "Integration")]
public class OpcUaResilienceTests
{
    private readonly ITestOutputHelper _output;

    // Fast configuration for resilience tests
    // Note: Stall detection triggers after StallDetectionIterations × SubscriptionHealthCheckInterval
    private readonly OpcUaTestClientConfiguration _fastClientConfig = new()
    {
        ReconnectInterval = TimeSpan.FromMilliseconds(500),
        ReconnectHandlerTimeout = TimeSpan.FromSeconds(2),
        SessionTimeout = TimeSpan.FromSeconds(10),
        SubscriptionHealthCheckInterval = TimeSpan.FromSeconds(2), // Fast health checks
        KeepAliveInterval = TimeSpan.FromSeconds(1), // Fast keep-alive for quick disconnection detection
        OperationTimeout = TimeSpan.FromSeconds(3), // Short timeout for fast failure detection
        StallDetectionIterations = 3 // Fast stall detection: 3 × 2s = 6s (instead of default 10 × 10s = 100s)
    };

    public OpcUaResilienceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private async Task<(OpcUaTestServer<TestRoot> Server, OpcUaTestClient<TestRoot> Client)> StartServerAndClientAsync()
    {
        var server = new OpcUaTestServer<TestRoot>(_output);
        await server.StartAsync(
            context => new TestRoot(context),
            (context, root) =>
            {
                root.Connected = true;
                root.Name = "Initial";
                root.Number = 42m;
            });

        var client = new OpcUaTestClient<TestRoot>(_output);
        await client.StartAsync(
            context => new TestRoot(context),
            isConnected: root => root.Connected,
            configuration: _fastClientConfig);

        return (server, client);
    }

    [Fact]
    public async Task ServerRestart_ClientFullyReconnects_SubscriptionsRecreated()
    {
        OpcUaTestServer<TestRoot>? server = null;
        OpcUaTestClient<TestRoot>? client = null;

        try
        {
            // Arrange - Start server and client, verify initial sync
            (server, client) = await StartServerAndClientAsync();

            Assert.NotNull(server.Root);
            Assert.NotNull(client.Root);
            Assert.NotNull(client.Diagnostics);

            // Verify initial connection
            Assert.True(client.Diagnostics.IsConnected, "Client should be connected initially");
            Assert.Equal("Initial", client.Root.Name);
            _output.WriteLine("Initial sync verified");

            // Act - Stop server completely
            _output.WriteLine("Stopping server...");
            await server.StopAsync();

            // Wait for client to detect disconnection
            // OPC UA uses keep-alive messages to detect server loss - this can take several seconds
            await AsyncTestHelpers.WaitUntilAsync(
                () => !client.Diagnostics.IsConnected,
                timeout: TimeSpan.FromSeconds(30),
                message: "Client should detect server disconnection");
            _output.WriteLine("Client detected disconnection");

            // Wait a bit longer to ensure session is truly dead
            await Task.Delay(TimeSpan.FromSeconds(2));

            // Restart server
            _output.WriteLine("Restarting server...");
            await server.RestartAsync();

            // Wait for client to reconnect
            // Note: Stall detection takes ~20s (10 × 2s health checks), plus reconnection time
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Diagnostics.IsConnected && !client.Diagnostics.IsReconnecting,
                timeout: TimeSpan.FromSeconds(20),
                message: "Client should reconnect after server restart");
            _output.WriteLine("Client reconnected");

            // Assert - Verify subscriptions are working by changing a value
            server.Root.Name = "AfterRestart";

            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.Name == "AfterRestart",
                timeout: TimeSpan.FromSeconds(10),
                message: "Property change should propagate after reconnection");

            _output.WriteLine($"Value propagated: {client.Root.Name}");
            Assert.Equal("AfterRestart", client.Root.Name);
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task ServerBrieflyUnavailable_ClientRecovers_DataFlowsContinue()
    {
        OpcUaTestServer<TestRoot>? server = null;
        OpcUaTestClient<TestRoot>? client = null;

        try
        {
            // Arrange - Start server and client
            (server, client) = await StartServerAndClientAsync();

            Assert.NotNull(server.Root);
            Assert.NotNull(client.Root);
            Assert.NotNull(client.Diagnostics);

            // Verify initial connection and sync
            Assert.True(client.Diagnostics.IsConnected);
            server.Root.Name = "BeforeDisconnect";
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.Name == "BeforeDisconnect",
                timeout: TimeSpan.FromSeconds(5));
            _output.WriteLine("Initial sync verified");

            var initialSessionId = client.Diagnostics.SessionId;
            _output.WriteLine($"Initial session ID: {initialSessionId}");

            // Act - Brief server restart (but still wait for client to detect)
            _output.WriteLine("Brief server restart...");
            await server.StopAsync();

            // Wait for client to detect disconnection
            // OPC UA uses keep-alive messages to detect server loss - this can take several seconds
            await AsyncTestHelpers.WaitUntilAsync(
                () => !client.Diagnostics.IsConnected,
                timeout: TimeSpan.FromSeconds(30),
                message: "Client should detect server disconnection");
            _output.WriteLine("Client detected disconnection");

            // Restart server quickly
            await server.RestartAsync();

            // Wait for client to recover
            // Note: Stall detection takes ~20s (10 × 2s health checks), plus reconnection time
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Diagnostics.IsConnected && !client.Diagnostics.IsReconnecting,
                timeout: TimeSpan.FromSeconds(20),
                message: "Client should recover after brief outage");

            var newSessionId = client.Diagnostics.SessionId;
            _output.WriteLine($"Session ID after recovery: {newSessionId}");

            // Assert - Verify data still flows
            server.Root.Name = "AfterBriefOutage";

            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.Name == "AfterBriefOutage",
                timeout: TimeSpan.FromSeconds(10),
                message: "Property change should propagate after recovery");

            _output.WriteLine($"Value propagated: {client.Root.Name}");
            Assert.Equal("AfterBriefOutage", client.Root.Name);
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task MultipleServerRestarts_ClientRecoveryEveryTime_NoStateCorruption()
    {
        OpcUaTestServer<TestRoot>? server = null;
        OpcUaTestClient<TestRoot>? client = null;

        try
        {
            // Arrange
            (server, client) = await StartServerAndClientAsync();

            Assert.NotNull(server.Root);
            Assert.NotNull(client.Root);
            Assert.NotNull(client.Diagnostics);

            // Act & Assert - Multiple restart cycles
            for (var cycle = 1; cycle <= 3; cycle++)
            {
                _output.WriteLine($"=== Restart cycle {cycle} ===");

                // Verify connection
                Assert.True(client.Diagnostics.IsConnected, $"Cycle {cycle}: Should be connected");

                // Update value and verify sync
                var testValue = $"Cycle{cycle}";
                server.Root.Name = testValue;

                await AsyncTestHelpers.WaitUntilAsync(
                    () => client.Root.Name == testValue,
                    timeout: TimeSpan.FromSeconds(10),
                    message: $"Cycle {cycle}: Value should propagate");

                Assert.Equal(testValue, client.Root.Name);
                _output.WriteLine($"Cycle {cycle}: Value propagated correctly");

                // Restart server
                await server.StopAsync();

                // OPC UA uses keep-alive messages to detect server loss - this can take several seconds
                await AsyncTestHelpers.WaitUntilAsync(
                    () => !client.Diagnostics.IsConnected,
                    timeout: TimeSpan.FromSeconds(30),
                    message: $"Cycle {cycle}: Client should detect disconnection");

                await server.RestartAsync();

                // Note: Stall detection takes ~20s (10 × 2s health checks), plus reconnection time
                await AsyncTestHelpers.WaitUntilAsync(
                    () => client.Diagnostics.IsConnected && !client.Diagnostics.IsReconnecting,
                    timeout: TimeSpan.FromSeconds(20),
                    message: $"Cycle {cycle}: Client should reconnect");

                _output.WriteLine($"Cycle {cycle}: Reconnected successfully");
            }

            // Final verification
            server.Root.Name = "FinalValue";
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.Name == "FinalValue",
                timeout: TimeSpan.FromSeconds(10),
                message: "Final value should propagate");

            Assert.Equal("FinalValue", client.Root.Name);
            _output.WriteLine("All cycles completed successfully");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task InstantServerRestart_ClientSelfCorrects_NoExplicitDisconnectionWait()
    {
        // This test verifies that even if the server restarts so quickly that the client
        // doesn't explicitly "detect" disconnection via IsConnected becoming false,
        // the client will still self-correct when its next operation fails with BadSessionIdInvalid.

        OpcUaTestServer<TestRoot>? server = null;
        OpcUaTestClient<TestRoot>? client = null;

        try
        {
            // Arrange - Start server and client
            (server, client) = await StartServerAndClientAsync();

            Assert.NotNull(server.Root);
            Assert.NotNull(client.Root);
            Assert.NotNull(client.Diagnostics);

            // Verify initial connection and sync
            Assert.True(client.Diagnostics.IsConnected);
            server.Root.Name = "BeforeInstantRestart";
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.Name == "BeforeInstantRestart",
                timeout: TimeSpan.FromSeconds(5));
            _output.WriteLine("Initial sync verified");

            // Act - Stop and IMMEDIATELY restart server (no wait for disconnection detection)
            _output.WriteLine("Instant server restart (no disconnection wait)...");
            await server.StopAsync();
            await server.RestartAsync(); // Immediate restart

            // The client may or may not detect the disconnection explicitly.
            // What matters is that data eventually flows again.
            // The client's next keep-alive will fail with BadSessionIdInvalid,
            // triggering reconnection.

            // Assert - Verify data flows after restart (client must self-correct)
            server.Root.Name = "AfterInstantRestart";

            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.Name == "AfterInstantRestart",
                timeout: TimeSpan.FromSeconds(30), // Allow time for keep-alive failure + stall detection + reconnection
                message: "Property change should propagate after instant restart (client self-corrects)");

            _output.WriteLine($"Value propagated: {client.Root.Name}");
            Assert.Equal("AfterInstantRestart", client.Root.Name);

            // Verify client is in a healthy state
            Assert.True(client.Diagnostics.IsConnected, "Client should be connected after self-correction");
            _output.WriteLine("Client self-corrected successfully");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
        }
    }
}
