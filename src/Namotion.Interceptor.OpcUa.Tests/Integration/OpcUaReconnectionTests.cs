using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Testing;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

/// <summary>
/// Tests for OPC UA client reconnection after server restarts.
/// </summary>
[Trait("Category", "Integration")]
public class OpcUaReconnectionTests
{
    private readonly ITestOutputHelper _output;

    public OpcUaReconnectionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private async Task<(OpcUaTestServer<TestRoot> Server, OpcUaTestClient<TestRoot> Client, PortLease Port, TestLogger Logger)> StartServerAndClientAsync()
    {
        var logger = new TestLogger(_output);
        var port = await OpcUaTestPortPool.AcquireAsync();

        var server = new OpcUaTestServer<TestRoot>(logger);
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

        var client = new OpcUaTestClient<TestRoot>(logger);
        await client.StartAsync(
            context => new TestRoot(context),
            isConnected: root => root.Connected,
            serverUrl: port.ServerUrl,
            certificateStoreBasePath: port.CertificateStoreBasePath);

        return (server, client, port, logger);
    }

    [Fact]
    public async Task ServerRestart_WithDisconnectionWait_ClientRecovers()
    {
        // Tests reconnection when client detects disconnection before server restarts.

        OpcUaTestServer<TestRoot>? server = null;
        OpcUaTestClient<TestRoot>? client = null;
        PortLease? port = null;
        TestLogger? logger = null;

        try
        {
            (server, client, port, logger) = await StartServerAndClientAsync();

            Assert.NotNull(server.Root);
            Assert.NotNull(client.Root);
            Assert.NotNull(client.Diagnostics);

            // Verify initial sync
            Assert.True(client.Diagnostics.IsConnected);
            server.Root.Name = "Initial";
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.Name == "Initial",
                timeout: TimeSpan.FromSeconds(60),
                message: "Initial sync should complete");
            logger.Log("Initial sync verified");

            var initialAttempts = client.Diagnostics.TotalReconnectionAttempts;

            // Stop server and wait for client to detect disconnection
            logger.Log("Stopping server...");
            await server.StopAsync();

            await AsyncTestHelpers.WaitUntilAsync(
                () => !client.Diagnostics.IsConnected,
                timeout: TimeSpan.FromSeconds(60),
                message: "Client should detect disconnection");
            logger.Log("Client detected disconnection");

            // Restart server
            await server.RestartAsync();

            // Verify data flows after reconnection
            server.Root.Name = "AfterRestart";
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.Name == "AfterRestart",
                timeout: TimeSpan.FromSeconds(90),
                message: "Data should flow after restart");
            logger.Log($"Client received: {client.Root.Name}");

            // Verify metrics
            var finalAttempts = client.Diagnostics.TotalReconnectionAttempts;
            var successfulReconnections = client.Diagnostics.SuccessfulReconnections;

            logger.Log($"Reconnection attempts: {initialAttempts} -> {finalAttempts}");
            logger.Log($"Successful reconnections: {successfulReconnections}");

            Assert.True(finalAttempts > initialAttempts,
                $"Reconnection attempts should have increased (was {initialAttempts}, now {finalAttempts})");
            Assert.True(successfulReconnections >= 1,
                $"Should have at least 1 successful reconnection, had {successfulReconnections}");

            logger.Log("Test passed");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }

    [Fact]
    public async Task ServerRestart_Instant_ClientRecovers()
    {
        // Tests reconnection when server restarts immediately (no time for client to detect disconnection).

        OpcUaTestServer<TestRoot>? server = null;
        OpcUaTestClient<TestRoot>? client = null;
        PortLease? port = null;
        TestLogger? logger = null;

        try
        {
            (server, client, port, logger) = await StartServerAndClientAsync();

            Assert.NotNull(server.Root);
            Assert.NotNull(client.Root);
            Assert.NotNull(client.Diagnostics);

            // Verify initial sync
            server.Root.Name = "Initial";
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.Name == "Initial",
                timeout: TimeSpan.FromSeconds(60),
                message: "Initial sync should complete");
            logger.Log("Initial sync verified");

            // Instant restart - no waiting for disconnection detection
            logger.Log("Instant server restart...");
            await server.StopAsync();
            await server.RestartAsync();

            // Verify data flows after reconnection
            server.Root.Name = "AfterInstantRestart";
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.Name == "AfterInstantRestart",
                timeout: TimeSpan.FromSeconds(90),
                message: "Data should flow after instant restart");
            logger.Log($"Client received: {client.Root.Name}");

            logger.Log("Test passed");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }

    [Fact]
    public async Task LargeSubscriptionCount_AllPropertiesResync()
    {
        // Verifies all subscriptions recreated after restart with many properties.

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
                    root.Name = "LargeSubscription";
                    root.Number = 100m;

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

            client = new OpcUaTestClient<TestRoot>(logger);
            await client.StartAsync(
                context => new TestRoot(context),
                isConnected: root => root.Connected,
                serverUrl: port.ServerUrl,
                certificateStoreBasePath: port.CertificateStoreBasePath);

            Assert.NotNull(server.Root);
            Assert.NotNull(client.Root);

            // Verify initial sync
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.People.Length == 20,
                timeout: TimeSpan.FromSeconds(60),
                message: "All people should sync");

            var monitoredCount = client.Diagnostics!.MonitoredItemCount;
            logger.Log($"Monitored items: {monitoredCount}");

            // Restart
            logger.Log("Stopping server...");
            await server.StopAsync();
            await AsyncTestHelpers.WaitUntilAsync(
                () => !client.Diagnostics.IsConnected,
                timeout: TimeSpan.FromSeconds(60),
                message: "Client should detect disconnection");
            logger.Log("Client detected disconnection");

            await server.RestartAsync();

            // Verify all properties resync
            server.Root.Name = "AfterRestart";
            server.Root.People[0].FirstName = "Updated0";
            server.Root.People[19].FirstName = "Updated19";

            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.Name == "AfterRestart" &&
                      client.Root.People[0].FirstName == "Updated0" &&
                      client.Root.People[19].FirstName == "Updated19",
                timeout: TimeSpan.FromSeconds(90),
                message: "All properties should resync");

            logger.Log($"All {client.Diagnostics.MonitoredItemCount} items resynced");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }
}
