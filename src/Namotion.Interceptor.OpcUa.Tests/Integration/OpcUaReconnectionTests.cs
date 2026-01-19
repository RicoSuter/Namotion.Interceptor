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
    public async Task ServerRestart_ClientRecoversAndMetricsAccumulate()
    {
        // Tests reconnection scenarios and metrics:
        // 1. Restart with explicit disconnection wait
        // 2. Instant restart without waiting for disconnection
        // 3. Verify reconnection metrics accumulated

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
                timeout: TimeSpan.FromSeconds(30),
                message: "Initial sync should complete");
            logger.Log("Initial sync verified");

            var initialAttempts = client.Diagnostics.TotalReconnectionAttempts;

            // === Test 1: Restart WITH disconnection wait ===
            logger.Log("=== Test 1: Restart with disconnection wait ===");
            await server.StopAsync();

            await AsyncTestHelpers.WaitUntilAsync(
                () => !client.Diagnostics.IsConnected,
                timeout: TimeSpan.FromSeconds(30),
                message: "Client should detect disconnection");
            logger.Log("Client detected disconnection");

            await server.RestartAsync();

            server.Root.Name = "AfterWaitRestart";
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.Name == "AfterWaitRestart",
                timeout: TimeSpan.FromSeconds(60),
                message: "Data should flow after restart with wait");
            logger.Log($"Test 1 passed: {client.Root.Name}");

            // === Test 2: INSTANT restart without wait ===
            logger.Log("=== Test 2: Instant restart without wait ===");
            await server.StopAsync();
            await server.RestartAsync(); // Immediate, no disconnection wait

            server.Root.Name = "AfterInstantRestart";
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.Name == "AfterInstantRestart",
                timeout: TimeSpan.FromSeconds(30),
                message: "Data should flow after instant restart");
            logger.Log($"Test 2 passed: {client.Root.Name}");

            // === Test 3: Verify metrics accumulated ===
            logger.Log("=== Test 3: Verify metrics ===");
            var finalAttempts = client.Diagnostics.TotalReconnectionAttempts;
            var successfulReconnections = client.Diagnostics.SuccessfulReconnections;

            logger.Log($"Reconnection attempts: {initialAttempts} -> {finalAttempts}");
            logger.Log($"Successful reconnections: {successfulReconnections}");

            Assert.True(finalAttempts > initialAttempts,
                $"Reconnection attempts should have increased (was {initialAttempts}, now {finalAttempts})");
            Assert.True(successfulReconnections >= 1,
                $"Should have at least 1 successful reconnection, had {successfulReconnections}");

            logger.Log("All tests passed");
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
                timeout: TimeSpan.FromSeconds(10),
                message: "All people should sync");

            var monitoredCount = client.Diagnostics!.MonitoredItemCount;
            logger.Log($"Monitored items: {monitoredCount}");

            // Restart
            await server.StopAsync();
            await AsyncTestHelpers.WaitUntilAsync(
                () => !client.Diagnostics.IsConnected,
                timeout: TimeSpan.FromSeconds(30));
            await server.RestartAsync();

            // Verify all properties resync
            server.Root.Name = "AfterRestart";
            server.Root.People[0].FirstName = "Updated0";
            server.Root.People[19].FirstName = "Updated19";

            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.Name == "AfterRestart" &&
                      client.Root.People[0].FirstName == "Updated0" &&
                      client.Root.People[19].FirstName == "Updated19",
                timeout: TimeSpan.FromSeconds(60),
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
