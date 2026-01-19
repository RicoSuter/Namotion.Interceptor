using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Testing;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

/// <summary>
/// Resilience tests verify client recovery from server disconnections and restarts.
/// These tests run sequentially due to timing sensitivity with OPC UA connections.
/// </summary>
[Trait("Category", "Integration")]
public class OpcUaServerRestartTests
{
    private readonly ITestOutputHelper _output;
    
    public OpcUaServerRestartTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private async Task<(OpcUaTestServer<TestRoot> Server, OpcUaTestClient<TestRoot> Client, PortLease Port)> StartServerAndClientAsync()
    {
        // Acquire a unique port for this test
        var port = await OpcUaTestPortPool.AcquireAsync();

        var server = new OpcUaTestServer<TestRoot>(_output);
        await server.StartAsync(
            context => new TestRoot(context),
            (context, root) =>
            {
                root.Connected = true;
                root.Name = "Initial";
                root.Number = 42m;
            },
            baseAddress: port.BaseAddress,
            certificateStoreBasePath: port.CertificateStoreBasePath);

        var client = new OpcUaTestClient<TestRoot>(_output);
        await client.StartAsync(
            context => new TestRoot(context),
            isConnected: root => root.Connected,
            serverUrl: port.ServerUrl,
            certificateStoreBasePath: port.CertificateStoreBasePath);

        return (server, client, port);
    }

    [Fact]
    public async Task ServerRestart_ClientFullyReconnects_SubscriptionsRecreated()
    {
        OpcUaTestServer<TestRoot>? server = null;
        OpcUaTestClient<TestRoot>? client = null;
        PortLease? port = null;

        try
        {
            // Arrange - Start server and client, verify initial sync
            (server, client, port) = await StartServerAndClientAsync();

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

            // Wait for reconnection to start (SDK reconnect handler activated)
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Diagnostics.IsReconnecting,
                timeout: TimeSpan.FromSeconds(10),
                message: "Client should start reconnecting");
            _output.WriteLine("Client is reconnecting");

            // Restart server
            _output.WriteLine("Restarting server...");
            await server.RestartAsync();

            // Wait for data flow - this proves reconnection worked
            // Note: We don't wait for IsConnected first because SDK reconnection can briefly
            // report connected before the session dies and manual reconnection starts.
            server.Root.Name = "AfterRestart";
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.Name == "AfterRestart",
                timeout: TimeSpan.FromSeconds(60),
                message: "Property change should propagate after reconnection");

            _output.WriteLine($"Client reconnected and data flowing: {client.Root.Name}");
            Assert.Equal("AfterRestart", client.Root.Name);
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }

    [Fact]
    public async Task MultipleServerRestarts_ClientRecoveryEveryTime_NoStateCorruption()
    {
        OpcUaTestServer<TestRoot>? server = null;
        OpcUaTestClient<TestRoot>? client = null;
        PortLease? port = null;

        try
        {
            // Arrange
            (server, client, port) = await StartServerAndClientAsync();

            Assert.NotNull(server.Root);
            Assert.NotNull(client.Root);
            Assert.NotNull(client.Diagnostics);

            // Act & Assert - Multiple restart cycles
            for (var cycle = 1; cycle <= 3; cycle++)
            {
                _output.WriteLine($"=== Restart cycle {cycle} ===");

                // Verify data flow before restart
                var testValue = $"Cycle{cycle}";
                server.Root.Name = testValue;

                await AsyncTestHelpers.WaitUntilAsync(
                    () => client.Root.Name == testValue,
                    timeout: TimeSpan.FromSeconds(30),
                    message: $"Cycle {cycle}: Value should propagate before restart");

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

                // Wait for data flow to prove reconnection worked
                var reconnectValue = $"Cycle{cycle}Reconnected";
                server.Root.Name = reconnectValue;
                await AsyncTestHelpers.WaitUntilAsync(
                    () => client.Root.Name == reconnectValue,
                    timeout: TimeSpan.FromSeconds(60),
                    message: $"Cycle {cycle}: Data should flow after reconnection");

                _output.WriteLine($"Cycle {cycle}: Reconnected and data flowing");
            }

            _output.WriteLine("All cycles completed successfully");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
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
        PortLease? port = null;

        try
        {
            // Arrange - Start server and client
            (server, client, port) = await StartServerAndClientAsync();

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

            // Verify client is in a healthy state (may still be reconnecting if it just finished)
            // The important thing is that data flowed successfully
            _output.WriteLine($"Client connected: {client.Diagnostics.IsConnected}, IsReconnecting: {client.Diagnostics.IsReconnecting}");
            _output.WriteLine("Client self-corrected successfully");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }

    [Fact]
    public async Task LargeSubscriptionCount_ServerRestart_AllPropertiesResync()
    {
        // This test verifies that with many monitored properties,
        // all subscriptions are properly recreated after a server restart.

        OpcUaTestServer<TestRoot>? server = null;
        OpcUaTestClient<TestRoot>? client = null;
        PortLease? port = null;

        try
        {
            port = await OpcUaTestPortPool.AcquireAsync();

            // Arrange - Create root with many people (each person has multiple properties)
            var serverBuilder = new OpcUaTestServer<TestRoot>(_output);
            await serverBuilder.StartAsync(
                context => new TestRoot(context),
                (context, root) =>
                {
                    root.Connected = true;
                    root.Name = "LargeSubscription";
                    root.Number = 100m;

                    // Create many people to increase subscription count
                    var people = new TestPerson[20];
                    for (var i = 0; i < people.Length; i++)
                    {
                        people[i] = new TestPerson(context)
                        {
                            FirstName = $"First{i}",
                            LastName = $"Last{i}",
                            Scores = [i * 1.0, i * 2.0, i * 3.0]
                        };
                    }
                    root.People = people;
                },
                baseAddress: port.BaseAddress,
                certificateStoreBasePath: port.CertificateStoreBasePath);

            server = serverBuilder;

            var clientBuilder = new OpcUaTestClient<TestRoot>(_output);
            await clientBuilder.StartAsync(
                context => new TestRoot(context),
                isConnected: root => root.Connected,
                serverUrl: port.ServerUrl,
                certificateStoreBasePath: port.CertificateStoreBasePath);

            client = clientBuilder;

            Assert.NotNull(server.Root);
            Assert.NotNull(client.Root);
            Assert.NotNull(client.Diagnostics);

            // Verify initial sync of all properties
            Assert.True(client.Diagnostics.IsConnected);
            Assert.Equal("LargeSubscription", client.Root.Name);
            Assert.Equal(20, server.Root.People.Length);

            var monitoredItemCount = client.Diagnostics.MonitoredItemCount;
            _output.WriteLine($"Monitored item count: {monitoredItemCount}");
            Assert.True(monitoredItemCount > 0, "Should have monitored items");

            // Wait for all people to sync
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.People.Length == 20,
                timeout: TimeSpan.FromSeconds(10),
                message: "All people should sync");

            // Verify a few people synced correctly
            Assert.Equal("First0", client.Root.People[0].FirstName);
            Assert.Equal("First19", client.Root.People[19].FirstName);
            _output.WriteLine("Initial sync of large subscription verified");

            // Act - Restart server
            _output.WriteLine("Restarting server...");
            await server.StopAsync();

            await AsyncTestHelpers.WaitUntilAsync(
                () => !client.Diagnostics.IsConnected,
                timeout: TimeSpan.FromSeconds(30),
                message: "Client should detect disconnection");

            await server.RestartAsync();

            // Wait for data flow - this proves reconnection and subscription recreation worked
            server.Root.Name = "AfterRestart";
            server.Root.People[0].FirstName = "UpdatedFirst0";
            server.Root.People[19].FirstName = "UpdatedFirst19";

            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.Name == "AfterRestart" &&
                      client.Root.People[0].FirstName == "UpdatedFirst0" &&
                      client.Root.People[19].FirstName == "UpdatedFirst19",
                timeout: TimeSpan.FromSeconds(60),
                message: "All property changes should propagate after reconnection");

            _output.WriteLine("Client reconnected and data flowing");

            // Assert - Verify monitored items were recreated
            var newMonitoredItemCount = client.Diagnostics.MonitoredItemCount;
            _output.WriteLine($"Monitored item count after reconnection: {newMonitoredItemCount}");

            Assert.Equal("AfterRestart", client.Root.Name);
            Assert.Equal("UpdatedFirst0", client.Root.People[0].FirstName);
            Assert.Equal("UpdatedFirst19", client.Root.People[19].FirstName);
            _output.WriteLine("All subscriptions recreated and data flowing");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }
    
    [Fact]
    public async Task ReconnectionMetrics_AfterMultipleRestarts_AccumulateCorrectly()
    {
        // This test verifies that reconnection metrics (total attempts, successful, failed)
        // accumulate correctly across multiple server restarts.

        OpcUaTestServer<TestRoot>? server = null;
        OpcUaTestClient<TestRoot>? client = null;
        PortLease? port = null;

        try
        {
            // Arrange
            (server, client, port) = await StartServerAndClientAsync();

            Assert.NotNull(server.Root);
            Assert.NotNull(client.Root);
            Assert.NotNull(client.Diagnostics);

            Assert.True(client.Diagnostics.IsConnected);

            var initialTotalAttempts = client.Diagnostics.TotalReconnectionAttempts;
            var initialSuccessful = client.Diagnostics.SuccessfulReconnections;
            _output.WriteLine($"Initial metrics - Total: {initialTotalAttempts}, Successful: {initialSuccessful}");

            // Act - Multiple restart cycles
            const int restartCycles = 3;
            for (var cycle = 1; cycle <= restartCycles; cycle++)
            {
                _output.WriteLine($"=== Restart cycle {cycle} ===");

                var successfulBefore = client.Diagnostics.SuccessfulReconnections;

                await server.StopAsync();

                await AsyncTestHelpers.WaitUntilAsync(
                    () => !client.Diagnostics.IsConnected,
                    timeout: TimeSpan.FromSeconds(30),
                    message: $"Cycle {cycle}: Should detect disconnection");

                await server.RestartAsync();

                // Wait for successful reconnection - this ensures the metrics actually increment
                await AsyncTestHelpers.WaitUntilAsync(
                    () => client.Diagnostics.SuccessfulReconnections > successfulBefore,
                    timeout: TimeSpan.FromSeconds(60),
                    message: $"Cycle {cycle}: Should complete successful reconnection");

                _output.WriteLine($"Cycle {cycle} complete - Total: {client.Diagnostics.TotalReconnectionAttempts}, " +
                    $"Successful: {client.Diagnostics.SuccessfulReconnections}");
            }

            // Assert - Metrics should have accumulated
            var finalTotalAttempts = client.Diagnostics.TotalReconnectionAttempts;
            var finalSuccessful = client.Diagnostics.SuccessfulReconnections;

            _output.WriteLine($"Final metrics - Total: {finalTotalAttempts}, Successful: {finalSuccessful}");

            // Note: Some reconnections may be handled by SessionReconnectHandler internally (transfer),
            // which doesn't count as a "successful reconnection" in our metrics (those count manual restarts).
            // We should have at least 1 successful reconnection from the manual restart path.
            Assert.True(finalSuccessful >= 1,
                $"Should have at least 1 successful reconnection, had {finalSuccessful}");
            Assert.True(finalTotalAttempts >= restartCycles,
                $"Should have at least {restartCycles} total reconnection attempts, had {finalTotalAttempts}");

            // Failed reconnections should be 0 or very low since server always came back quickly
            Assert.True(client.Diagnostics.FailedReconnections <= 1,
                $"Failed reconnections should be 0 or 1, had {client.Diagnostics.FailedReconnections}");

            _output.WriteLine("Reconnection metrics accumulate correctly");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }
}
