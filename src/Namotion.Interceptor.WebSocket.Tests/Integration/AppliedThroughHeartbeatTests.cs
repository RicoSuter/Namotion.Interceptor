using System;
using System.Threading;
using System.Threading.Tasks;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.WebSocket.Server;
using Xunit;
using Xunit.Abstractions;

namespace Namotion.Interceptor.WebSocket.Tests.Integration;

/// <summary>
/// Covers the applied-through count the server reports on the heartbeat, which retires in-flight
/// entries the server has confirmed applying rather than carrying them until the next reconnect.
/// </summary>
[Trait("Category", "Integration")]
public class AppliedThroughHeartbeatTests
{
    private readonly ITestOutputHelper _output;

    public AppliedThroughHeartbeatTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task WhenTheServerHasAppliedTheClientsUpdates_ThenTheHeartbeatRetiresTheInFlightEntries()
    {
        // Arrange: a connected client, with the server's heartbeat interval short enough to fire in-test.
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);
        await using var client = new WebSocketTestClient<TestRoot>(_output);

        await server.StartAsync(
            context => new TestRoot(context),
            (_, root) => root.Name = "Initial",
            port: portLease.Port,
            configureServer: configuration => configuration.HeartbeatInterval = WebSocketServerConfiguration.MinimumHeartbeatInterval);
        await client.StartAsync(context => new TestRoot(context), port: portLease.Port);
        await AsyncTestHelpers.WaitUntilAsync(() => client.Root!.Name == "Initial");

        // Act
        client.Root!.Name = "Written";
        await AsyncTestHelpers.WaitUntilAsync(() => server.Root!.Name == "Written");

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Source!.InFlightCount == 0,
            message: "The applied-through heartbeat should retire the in-flight entry");
    }

    [Fact]
    public async Task WhenTheServerAcknowledgedAWrite_ThenAReconnectDoesNotReAssertItOverANewerValue()
    {
        // Arrange: the client writes, the server applies it and reports it on a heartbeat, so the entry
        // is retired rather than carried.
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);
        await using var client = new WebSocketTestClient<TestRoot>(_output);

        await server.StartAsync(
            context => new TestRoot(context),
            (_, root) => root.Name = "Initial",
            port: portLease.Port,
            configureServer: configuration => configuration.HeartbeatInterval = WebSocketServerConfiguration.MinimumHeartbeatInterval);
        await client.StartAsync(context => new TestRoot(context), port: portLease.Port);
        await AsyncTestHelpers.WaitUntilAsync(() => client.Root!.Name == "Initial");

        client.Root!.Name = "AcknowledgedByServer";
        await AsyncTestHelpers.WaitUntilAsync(() => server.Root!.Name == "AcknowledgedByServer");
        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Source!.InFlightCount == 0,
            message: "The write should have been retired before the outage");

        // Act: the value moves on at the server while the client is away, then the client reconnects.
        await ((IFaultInjectable)client.Source!).InjectFaultAsync(FaultType.Disconnect, CancellationToken.None);
        server.Root!.Name = "NewerServerValue";

        // Assert: nothing is re-asserted, because there is no in-flight entry left to re-park. If it
        // were, the client's reconnect would send it back over the newer server value.
        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Root!.Name == "NewerServerValue",
            timeout: TimeSpan.FromSeconds(30),
            message: "Client should reconnect and converge on the value the server moved to while it was away");
        Assert.Equal("NewerServerValue", server.Root!.Name);
    }
}
