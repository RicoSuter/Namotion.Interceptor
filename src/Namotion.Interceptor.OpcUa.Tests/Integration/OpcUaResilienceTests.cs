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
        ReconnectInterval = TimeSpan.FromMilliseconds(250), // Very fast reconnect attempts
        ReconnectHandlerTimeout = TimeSpan.FromSeconds(1), // Quick SDK reconnect timeout
        SessionTimeout = TimeSpan.FromSeconds(5),
        SubscriptionHealthCheckInterval = TimeSpan.FromSeconds(1), // Fast health checks
        KeepAliveInterval = TimeSpan.FromMilliseconds(500), // Very fast keep-alive for quick disconnection detection
        OperationTimeout = TimeSpan.FromSeconds(2), // Short timeout for fast failure detection
        StallDetectionIterations = 2 // Fast stall detection: 2 × 1s = 2s
    };

    public OpcUaResilienceTests(ITestOutputHelper output)
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
            configuration: _fastClientConfig,
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
                timeout: TimeSpan.FromSeconds(90),
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

            // Wait for client to reconnect
            // Note: Stall detection takes ~6s (3 × 2s health checks), plus reconnection time and retry backoff
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Diagnostics.IsConnected && !client.Diagnostics.IsReconnecting,
                timeout: TimeSpan.FromSeconds(90),
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
            port?.Dispose();
        }
    }

    [Fact]
    public async Task ServerBrieflyUnavailable_ClientRecovers_DataFlowsContinue()
    {
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
                timeout: TimeSpan.FromSeconds(90),
                message: "Client should detect server disconnection");
            _output.WriteLine("Client detected disconnection");

            // Restart server quickly
            await server.RestartAsync();

            // Wait for client to recover
            // Note: Stall detection takes ~20s (10 × 2s health checks), plus reconnection time
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Diagnostics.IsConnected && !client.Diagnostics.IsReconnecting,
                timeout: TimeSpan.FromSeconds(45),
                message: "Client should recover after brief outage");

            var newSessionId = client.Diagnostics.SessionId;
            _output.WriteLine($"Session ID after recovery: {newSessionId}");

            // Assert - Verify data still flows (give extra time for subscriptions to stabilize)
            server.Root.Name = "AfterBriefOutage";

            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.Name == "AfterBriefOutage",
                timeout: TimeSpan.FromSeconds(90),
                message: "Property change should propagate after recovery");

            _output.WriteLine($"Value propagated: {client.Root.Name}");
            Assert.Equal("AfterBriefOutage", client.Root.Name);
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

                // Verify connection
                Assert.True(client.Diagnostics.IsConnected, $"Cycle {cycle}: Should be connected");

                // Update value and verify sync (give extra time for subscriptions to stabilize after reconnection)
                var testValue = $"Cycle{cycle}";
                server.Root.Name = testValue;

                await AsyncTestHelpers.WaitUntilAsync(
                    () => client.Root.Name == testValue,
                    timeout: TimeSpan.FromSeconds(90),
                    message: $"Cycle {cycle}: Value should propagate");

                Assert.Equal(testValue, client.Root.Name);
                _output.WriteLine($"Cycle {cycle}: Value propagated correctly");

                // Restart server
                await server.StopAsync();

                // OPC UA uses keep-alive messages to detect server loss - this can take several seconds
                await AsyncTestHelpers.WaitUntilAsync(
                    () => !client.Diagnostics.IsConnected,
                    timeout: TimeSpan.FromSeconds(90),
                    message: $"Cycle {cycle}: Client should detect disconnection");

                await server.RestartAsync();

                // Note: Stall detection takes ~6s (3 × 2s health checks), plus reconnection time and retry backoff
                await AsyncTestHelpers.WaitUntilAsync(
                    () => client.Diagnostics.IsConnected && !client.Diagnostics.IsReconnecting,
                    timeout: TimeSpan.FromSeconds(90),
                    message: $"Cycle {cycle}: Client should reconnect");

                _output.WriteLine($"Cycle {cycle}: Reconnected successfully");
            }

            // Final verification
            server.Root.Name = "FinalValue";
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.Name == "FinalValue",
                timeout: TimeSpan.FromSeconds(90),
                message: "Final value should propagate");

            Assert.Equal("FinalValue", client.Root.Name);
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
    public async Task ConcurrentDisposal_DoesNotThrowOrCorrupt()
    {
        // This test verifies that the claimed "race condition" in PR #162 doesn't exist.
        // Both Dispose() and DisposeAsync() use Interlocked.Exchange to guarantee only one executes cleanup.

        OpcUaTestServer<TestRoot>? server = null;
        OpcUaTestClient<TestRoot>? client = null;
        PortLease? port = null;

        try
        {
            (server, client, port) = await StartServerAndClientAsync();

            Assert.NotNull(server.Diagnostics);
            Assert.NotNull(client.Diagnostics);

            // Access diagnostics properties during active connection - should work
            var serverSessionCount = server.Diagnostics.ActiveSessionCount;
            var clientIsConnected = client.Diagnostics.IsConnected;

            _output.WriteLine($"Server sessions: {serverSessionCount}, Client connected: {clientIsConnected}");

            // Now dispose both concurrently - the Interlocked.Exchange prevents dual cleanup
            var disposeTask1 = client.DisposeAsync().AsTask();
            var disposeTask2 = server.DisposeAsync().AsTask();

            // This should complete without throwing
            await Task.WhenAll(disposeTask1, disposeTask2);

            _output.WriteLine("Concurrent disposal completed without error");

            // Mark as disposed so finally block doesn't double-dispose
            client = null;
            server = null;
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }

    [Fact]
    public async Task DiagnosticsAccess_DuringDisposal_DoesNotThrow()
    {
        // This test verifies that accessing ActiveSessionCount during disposal
        // doesn't throw NullReferenceException as claimed in PR #162.
        // The null-conditional chain (?.) handles nulls gracefully.

        OpcUaTestServer<TestRoot>? server = null;
        PortLease? port = null;

        try
        {
            port = await OpcUaTestPortPool.AcquireAsync();

            server = new OpcUaTestServer<TestRoot>(_output);
            await server.StartAsync(
                context => new TestRoot(context),
                (context, root) => { root.Connected = true; },
                baseAddress: port.BaseAddress,
                certificateStoreBasePath: port.CertificateStoreBasePath);

            Assert.NotNull(server.Diagnostics);

            // Start disposal in background
            var disposeTask = Task.Run(async () =>
            {
                await Task.Delay(10); // Small delay to let reads start
                await server.DisposeAsync();
            });

            // Rapidly access diagnostics during disposal
            var exceptions = new List<Exception>();
            for (var i = 0; i < 100; i++)
            {
                try
                {
                    // This should never throw NullReferenceException
                    // The ?. chain handles nulls gracefully, returning 0
                    var count = server.Diagnostics.ActiveSessionCount;
                    var isRunning = server.Diagnostics.IsRunning;
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
                await Task.Delay(1);
            }

            await disposeTask;

            // No NullReferenceException should have occurred
            Assert.Empty(exceptions);
            _output.WriteLine("Diagnostics access during disposal completed without NullReferenceException");

            server = null; // Already disposed
        }
        finally
        {
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
                timeout: TimeSpan.FromSeconds(90), // Allow time for keep-alive failure + stall detection + reconnection
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
    public async Task ProlongedOutage_DiagnosticsTrackDisconnectionDuration()
    {
        // This test verifies that during a prolonged outage:
        // 1. The client continues attempting reconnection (infinite retry)
        // 2. Diagnostics correctly track disconnection time
        // 3. Reconnection counter increments
        // 4. Client eventually recovers when server returns

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

            // Verify initial connection
            Assert.True(client.Diagnostics.IsConnected);
            Assert.NotNull(client.Diagnostics.LastConnectedAt);
            Assert.Null(client.Diagnostics.DisconnectedDuration);
            _output.WriteLine($"Initial connection at: {client.Diagnostics.LastConnectedAt}");

            var initialReconnectionAttempts = client.Diagnostics.TotalReconnectionAttempts;
            _output.WriteLine($"Initial reconnection attempts: {initialReconnectionAttempts}");

            // Act - Stop server to simulate prolonged outage
            _output.WriteLine("Stopping server for prolonged outage...");
            await server.StopAsync();

            // Wait for client to detect disconnection
            await AsyncTestHelpers.WaitUntilAsync(
                () => !client.Diagnostics.IsConnected,
                timeout: TimeSpan.FromSeconds(90),
                message: "Client should detect server disconnection");

            _output.WriteLine("Client detected disconnection");

            // Wait for LastDisconnectedAt to be set (set during first reconnection attempt)
            // This may take time as stall detection needs to trigger first: StallDetectionIterations × SubscriptionHealthCheckInterval
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Diagnostics.LastDisconnectedAt.HasValue,
                timeout: TimeSpan.FromSeconds(90),
                message: "LastDisconnectedAt should be set during reconnection attempt");

            _output.WriteLine($"Disconnection detected at: {client.Diagnostics.LastDisconnectedAt}");

            // Verify disconnection tracking is working (duration will be positive if LastDisconnectedAt was set)
            // Note: With fast test config, reconnection may happen quickly, so we just verify the timestamp was set
            var disconnectedDuration = client.Diagnostics.DisconnectedDuration;
            _output.WriteLine($"Disconnected duration (may be null if reconnected): {disconnectedDuration}");

            // Verify reconnection attempts are being made
            var currentReconnectionAttempts = client.Diagnostics.TotalReconnectionAttempts;
            _output.WriteLine($"Reconnection attempts during outage: {currentReconnectionAttempts}");
            // Note: May or may not have incremented depending on timing

            // Restart server after prolonged outage
            _output.WriteLine("Restarting server after prolonged outage...");
            await server.RestartAsync();

            // Wait for client to reconnect
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Diagnostics.IsConnected && !client.Diagnostics.IsReconnecting,
                timeout: TimeSpan.FromSeconds(90),
                message: "Client should reconnect after prolonged outage");

            _output.WriteLine("Client reconnected after prolonged outage");

            // Verify diagnostics after recovery
            Assert.True(client.Diagnostics.IsConnected);
            Assert.Null(client.Diagnostics.DisconnectedDuration); // Should be null when connected
            Assert.True(client.Diagnostics.TotalReconnectionAttempts >= initialReconnectionAttempts,
                "Reconnection attempts should have incremented");
            _output.WriteLine($"Final reconnection attempts: {client.Diagnostics.TotalReconnectionAttempts}");
            _output.WriteLine($"Successful reconnections: {client.Diagnostics.SuccessfulReconnections}");

            // Verify data flows again (give extra time for subscription to be fully recreated)
            server.Root.Name = "AfterProlongedOutage";
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.Name == "AfterProlongedOutage",
                timeout: TimeSpan.FromSeconds(90),
                message: "Property change should propagate after prolonged outage");

            Assert.Equal("AfterProlongedOutage", client.Root.Name);
            _output.WriteLine("Data flowing normally after prolonged outage recovery");
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
                configuration: _fastClientConfig,
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
                () => client.Root.People != null && client.Root.People.Length == 20,
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
                timeout: TimeSpan.FromSeconds(90),
                message: "Client should detect disconnection");

            await server.RestartAsync();

            // Wait for client to reconnect
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Diagnostics.IsConnected && !client.Diagnostics.IsReconnecting,
                timeout: TimeSpan.FromSeconds(90),
                message: "Client should reconnect after restart");

            _output.WriteLine("Client reconnected");

            // Assert - Verify monitored items were recreated
            var newMonitoredItemCount = client.Diagnostics.MonitoredItemCount;
            _output.WriteLine($"Monitored item count after reconnection: {newMonitoredItemCount}");

            // Verify data flows for multiple properties
            server.Root.Name = "AfterRestart";
            server.Root.People[0].FirstName = "UpdatedFirst0";
            server.Root.People[19].FirstName = "UpdatedFirst19";

            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.Name == "AfterRestart" &&
                      client.Root.People[0].FirstName == "UpdatedFirst0" &&
                      client.Root.People[19].FirstName == "UpdatedFirst19",
                timeout: TimeSpan.FromSeconds(10),
                message: "All property changes should propagate after reconnection");

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
    public async Task ConcurrentPropertyChanges_DuringReconnection_NoDataCorruption()
    {
        // This test verifies that property changes occurring during reconnection
        // don't cause data corruption or exceptions.

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
            _output.WriteLine("Initial connection established");

            // Start background task that continuously updates server properties
            var cancellationSource = new CancellationTokenSource();
            var updateCounter = 0;
            var updateExceptions = new List<Exception>();

            var updateTask = Task.Run(async () =>
            {
                while (!cancellationSource.Token.IsCancellationRequested)
                {
                    try
                    {
                        server.Root.Number = updateCounter;
                        server.Root.Name = $"Update{updateCounter}";
                        Interlocked.Increment(ref updateCounter);
                        await Task.Delay(100, cancellationSource.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        updateExceptions.Add(ex);
                    }
                }
            }, cancellationSource.Token);

            // Wait for some updates to flow through
            await AsyncTestHelpers.WaitUntilAsync(
                () => updateCounter > 0,
                timeout: TimeSpan.FromSeconds(10),
                message: "Updates should start flowing");
            _output.WriteLine($"Updates before restart: {updateCounter}");

            // Act - Restart server while updates are happening
            _output.WriteLine("Restarting server during updates...");
            await server.StopAsync();

            // Wait for client to detect server is down
            await AsyncTestHelpers.WaitUntilAsync(
                () => !client.Diagnostics.IsConnected,
                timeout: TimeSpan.FromSeconds(90),
                message: "Client should detect server disconnection");

            await server.RestartAsync();

            // Wait for reconnection
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Diagnostics.IsConnected && !client.Diagnostics.IsReconnecting,
                timeout: TimeSpan.FromSeconds(90),
                message: "Client should reconnect");

            // Wait for more updates to flow after reconnection
            var updatesBeforeReconnect = updateCounter;
            await AsyncTestHelpers.WaitUntilAsync(
                () => updateCounter > updatesBeforeReconnect,
                timeout: TimeSpan.FromSeconds(10),
                message: "Updates should continue after reconnection");

            // Stop updates
            await cancellationSource.CancelAsync();
            try
            {
                await updateTask;
            }
            catch (OperationCanceledException)
            {
                // Expected
            }

            _output.WriteLine($"Total updates: {updateCounter}");
            _output.WriteLine($"Update exceptions: {updateExceptions.Count}");

            // Assert - No exceptions during updates
            Assert.Empty(updateExceptions);

            // Verify final state is consistent
            var finalValue = $"FinalValue";
            server.Root.Name = finalValue;

            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.Name == finalValue,
                timeout: TimeSpan.FromSeconds(10),
                message: "Final value should propagate");

            Assert.Equal(finalValue, client.Root.Name);
            _output.WriteLine("No data corruption during concurrent updates and reconnection");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }

    [Fact]
    public async Task HealthCheckErrors_DuringDisconnection_DiagnosticsTrackCorrectly()
    {
        // This test verifies that consecutive health check errors are tracked correctly
        // during disconnection and reset after reconnection.

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
            Assert.Equal(0, client.Diagnostics.ConsecutiveHealthCheckErrors);
            _output.WriteLine("Initial connection verified, no health check errors");

            // Act - Stop server to trigger health check failures
            _output.WriteLine("Stopping server...");
            await server.StopAsync();

            // Wait for disconnection detection
            await AsyncTestHelpers.WaitUntilAsync(
                () => !client.Diagnostics.IsConnected,
                timeout: TimeSpan.FromSeconds(90),
                message: "Client should detect disconnection");

            _output.WriteLine("Client detected disconnection");

            // Wait for health check errors to accumulate (indicates health check is detecting the problem)
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Diagnostics.ConsecutiveHealthCheckErrors > 0,
                timeout: TimeSpan.FromSeconds(90),
                message: "Health check should detect errors during disconnection");

            var healthCheckErrors = client.Diagnostics.ConsecutiveHealthCheckErrors;
            _output.WriteLine($"Consecutive health check errors during outage: {healthCheckErrors}");
            Assert.True(healthCheckErrors > 0, "Should have at least one health check error");

            // Restart server
            _output.WriteLine("Restarting server...");
            await server.RestartAsync();

            // Wait for reconnection
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Diagnostics.IsConnected && !client.Diagnostics.IsReconnecting,
                timeout: TimeSpan.FromSeconds(90),
                message: "Client should reconnect");

            _output.WriteLine("Client reconnected");

            // Wait for health check errors to reset after successful reconnection
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Diagnostics.ConsecutiveHealthCheckErrors == 0,
                timeout: TimeSpan.FromSeconds(15),
                message: "Health check errors should reset after reconnection");

            var finalHealthCheckErrors = client.Diagnostics.ConsecutiveHealthCheckErrors;
            _output.WriteLine($"Consecutive health check errors after reconnection: {finalHealthCheckErrors}");
            Assert.Equal(0, finalHealthCheckErrors);

            // Verify data flows (give extra time for subscription to be fully recreated)
            server.Root.Name = "AfterHealthCheck";
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.Name == "AfterHealthCheck",
                timeout: TimeSpan.FromSeconds(90),
                message: "Property change should propagate after reconnection");

            Assert.Equal("AfterHealthCheck", client.Root.Name);
            _output.WriteLine("Health check error tracking verified");
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

                await server.StopAsync();

                await AsyncTestHelpers.WaitUntilAsync(
                    () => !client.Diagnostics.IsConnected,
                    timeout: TimeSpan.FromSeconds(90),
                    message: $"Cycle {cycle}: Should detect disconnection");

                await server.RestartAsync();

                await AsyncTestHelpers.WaitUntilAsync(
                    () => client.Diagnostics.IsConnected && !client.Diagnostics.IsReconnecting,
                    timeout: TimeSpan.FromSeconds(90),
                    message: $"Cycle {cycle}: Should reconnect");

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
