using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Testing;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

/// <summary>
/// Resilience tests verify client recovery from server disconnections and restarts.
/// These tests run sequentially due to timing sensitivity with OPC UA connections.
/// </summary>
[Trait("Category", "Integration")]
public class OpcUaResilienceTests
{
    private readonly ITestOutputHelper _output;
    
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
            serverUrl: port.ServerUrl,
            certificateStoreBasePath: port.CertificateStoreBasePath);

        return (server, client, port);
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

            // Verify initial connection and sync (longer timeout for parallel execution load)
            Assert.True(client.Diagnostics.IsConnected);
            server.Root.Name = "BeforeDisconnect";
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.Name == "BeforeDisconnect",
                timeout: TimeSpan.FromSeconds(30),
                message: "Initial sync should complete");
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

            // Wait for data flow - this proves reconnection worked
            server.Root.Name = "AfterBriefOutage";
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.Name == "AfterBriefOutage",
                timeout: TimeSpan.FromSeconds(60),
                message: "Property change should propagate after recovery");

            var newSessionId = client.Diagnostics.SessionId;
            _output.WriteLine($"Session ID after recovery: {newSessionId}");
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
    public async Task ProlongedOutage_DiagnosticsTrackReconnectionAttempts()
    {
        // This test verifies that during a prolonged outage:
        // 1. The client continues attempting reconnection (infinite retry)
        // 2. Reconnection counter increments
        // 3. Client eventually recovers when server returns

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
            _output.WriteLine($"Initial connection at: {client.Diagnostics.LastConnectedAt}");

            var initialReconnectionAttempts = client.Diagnostics.TotalReconnectionAttempts;
            _output.WriteLine($"Initial reconnection attempts: {initialReconnectionAttempts}");

            // Act - Stop server to simulate prolonged outage
            _output.WriteLine("Stopping server for prolonged outage...");
            await server.StopAsync();

            // Wait for client to detect disconnection
            await AsyncTestHelpers.WaitUntilAsync(
                () => !client.Diagnostics.IsConnected,
                timeout: TimeSpan.FromSeconds(30),
                message: "Client should detect server disconnection");

            _output.WriteLine("Client detected disconnection");

            // Wait for reconnection attempts to start
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Diagnostics.TotalReconnectionAttempts > initialReconnectionAttempts,
                timeout: TimeSpan.FromSeconds(30),
                message: "Reconnection attempts should start");

            var currentReconnectionAttempts = client.Diagnostics.TotalReconnectionAttempts;
            _output.WriteLine($"Reconnection attempts during outage: {currentReconnectionAttempts}");

            // Restart server after prolonged outage
            _output.WriteLine("Restarting server after prolonged outage...");
            await server.RestartAsync();

            // Wait for data flow - this proves reconnection worked
            // Note: We don't wait for IsConnected first because SDK reconnection can briefly
            // report connected before the session dies and manual reconnection starts.
            // Waiting for data flow is more reliable and tests what we actually care about.
            server.Root.Name = "AfterProlongedOutage";
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.Name == "AfterProlongedOutage",
                timeout: TimeSpan.FromSeconds(60),
                message: "Property change should propagate after prolonged outage");

            _output.WriteLine("Client reconnected and data flowing after prolonged outage");

            // Verify diagnostics after recovery
            Assert.True(client.Diagnostics.IsConnected);
            Assert.True(client.Diagnostics.TotalReconnectionAttempts >= initialReconnectionAttempts,
                "Reconnection attempts should have incremented");
            _output.WriteLine($"Final reconnection attempts: {client.Diagnostics.TotalReconnectionAttempts}");
            _output.WriteLine($"Successful reconnections: {client.Diagnostics.SuccessfulReconnections}");

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
                timeout: TimeSpan.FromSeconds(30),
                message: "Client should detect server disconnection");

            await server.RestartAsync();

            // Wait for updates to continue flowing - this proves reconnection worked
            var updatesBeforeReconnect = updateCounter;
            await AsyncTestHelpers.WaitUntilAsync(
                () => updateCounter > updatesBeforeReconnect,
                timeout: TimeSpan.FromSeconds(60),
                message: "Updates should continue after reconnection");

            _output.WriteLine("Client reconnected and updates flowing");

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
    public async Task ServerDisconnection_ClientRecoversAutomatically()
    {
        // This test verifies that the client automatically recovers from server disconnection.

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
            _output.WriteLine("Initial connection verified");

            // Act - Stop server to trigger disconnection
            _output.WriteLine("Stopping server...");
            await server.StopAsync();

            // Wait for disconnection detection
            await AsyncTestHelpers.WaitUntilAsync(
                () => !client.Diagnostics.IsConnected,
                timeout: TimeSpan.FromSeconds(30),
                message: "Client should detect disconnection");

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
            server.Root.Name = "AfterRecovery";
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.Name == "AfterRecovery",
                timeout: TimeSpan.FromSeconds(60),
                message: "Property change should propagate after reconnection");

            _output.WriteLine("Client reconnected and data flowing");
            Assert.Equal("AfterRecovery", client.Root.Name);
            _output.WriteLine("Automatic recovery verified");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }
}
