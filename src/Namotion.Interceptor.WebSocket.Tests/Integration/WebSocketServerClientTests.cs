using System;
using System.Threading.Tasks;
using Namotion.Interceptor.Testing;
using Xunit;
using Xunit.Abstractions;

namespace Namotion.Interceptor.WebSocket.Tests.Integration;

/// <summary>
/// Integration tests for the standalone WebSocket server (AddWebSocketSubjectServer).
/// </summary>
[Trait("Category", "Integration")]
public class WebSocketServerClientTests
{
    private readonly ITestOutputHelper _output;

    public WebSocketServerClientTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task ServerWriteProperty_ShouldUpdateClient()
    {
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);
        await using var client = new WebSocketTestClient<TestRoot>(_output);

        await server.StartAsync(
            context => new TestRoot(context),
            (_, root) => root.Name = "Initial",
            port: portLease.Port);

        await client.StartAsync(context => new TestRoot(context), port: portLease.Port);

        // Wait for initial sync
        await Task.Delay(500);
        Assert.Equal("Initial", client.Root!.Name);

        // Server updates property
        server.Root!.Name = "Updated from Server";
        await Task.Delay(500);

        Assert.Equal("Updated from Server", client.Root.Name);
    }

    [Fact]
    public async Task ClientWriteProperty_ShouldUpdateServer()
    {
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);
        await using var client = new WebSocketTestClient<TestRoot>(_output);

        await server.StartAsync(context => new TestRoot(context), port: portLease.Port);
        await client.StartAsync(context => new TestRoot(context), port: portLease.Port);

        // Client updates property
        client.Root!.Name = "Updated from Client";
        await Task.Delay(500);

        Assert.Equal("Updated from Client", server.Root!.Name);
    }

    [Fact]
    public async Task NumericProperty_ShouldSyncBidirectionally()
    {
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);
        await using var client = new WebSocketTestClient<TestRoot>(_output);

        await server.StartAsync(context => new TestRoot(context), port: portLease.Port);
        await client.StartAsync(context => new TestRoot(context), port: portLease.Port);

        // Server updates
        server.Root!.Number = 123.45m;
        await Task.Delay(500);
        Assert.Equal(123.45m, client.Root!.Number);

        // Client updates
        client.Root.Number = 678.90m;
        await Task.Delay(500);
        Assert.Equal(678.90m, server.Root.Number);
    }

    [Fact]
    public async Task MultipleClients_ShouldAllReceiveUpdates()
    {
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);
        await using var client1 = new WebSocketTestClient<TestRoot>(_output);
        await using var client2 = new WebSocketTestClient<TestRoot>(_output);

        await server.StartAsync(context => new TestRoot(context), port: portLease.Port);
        await client1.StartAsync(context => new TestRoot(context), port: portLease.Port);
        await client2.StartAsync(context => new TestRoot(context), port: portLease.Port);

        server.Root!.Name = "Broadcast Test";
        await Task.Delay(500);

        Assert.Equal("Broadcast Test", client1.Root!.Name);
        Assert.Equal("Broadcast Test", client2.Root!.Name);
    }

    [Fact]
    public async Task ServerRestart_WithDisconnectionWait_ClientRecovers()
    {
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);
        await using var client = new WebSocketTestClient<TestRoot>(_output);

        await server.StartAsync(
            context => new TestRoot(context),
            (_, root) => root.Name = "Initial",
            port: portLease.Port);

        await client.StartAsync(context => new TestRoot(context), port: portLease.Port);

        // Wait for initial sync
        await Task.Delay(500);
        Assert.Equal("Initial", client.Root!.Name);
        _output.WriteLine("Initial sync verified");

        // Stop server and wait for client to detect disconnection
        _output.WriteLine("Stopping server...");
        await server.StopAsync();

        // Wait for client to detect disconnection
        await Task.Delay(2000);
        _output.WriteLine("Client should have detected disconnection");

        // Restart server
        _output.WriteLine("Restarting server...");
        await server.RestartAsync();

        // Update server state after restart
        server.Root!.Name = "AfterRestart";

        // Wait for client to reconnect and sync
        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Root!.Name == "AfterRestart",
            timeout: TimeSpan.FromSeconds(30),
            message: "Client should receive update after server restart");

        _output.WriteLine($"Client received: {client.Root.Name}");
        Assert.Equal("AfterRestart", client.Root.Name);
    }

    [Fact]
    public async Task ServerRestart_Instant_ClientRecovers()
    {
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);
        await using var client = new WebSocketTestClient<TestRoot>(_output);

        await server.StartAsync(
            context => new TestRoot(context),
            (_, root) => root.Name = "Initial",
            port: portLease.Port);

        await client.StartAsync(context => new TestRoot(context), port: portLease.Port);

        // Wait for initial sync
        await Task.Delay(500);
        Assert.Equal("Initial", client.Root!.Name);
        _output.WriteLine("Initial sync verified");

        // Instant restart - no waiting for disconnection detection
        _output.WriteLine("Instant server restart...");
        await server.StopAsync();
        await server.RestartAsync();

        // Update server state after restart
        server.Root!.Name = "AfterInstantRestart";

        // Wait for client to reconnect and sync
        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Root!.Name == "AfterInstantRestart",
            timeout: TimeSpan.FromSeconds(30),
            message: "Client should receive update after instant restart");

        _output.WriteLine($"Client received: {client.Root.Name}");
        Assert.Equal("AfterInstantRestart", client.Root.Name);
    }

    [Fact]
    public async Task ServerRestart_WithCollectionItems_AllPropertiesResync()
    {
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);
        await using var client = new WebSocketTestClient<TestRoot>(_output);

        await server.StartAsync(
            context => new TestRoot(context),
            (context, root) =>
            {
                root.Name = "WithItems";
                root.Number = 100m;
                root.Items =
                [
                    new TestItem(context) { Label = "Item1", Value = 10 },
                    new TestItem(context) { Label = "Item2", Value = 20 },
                    new TestItem(context) { Label = "Item3", Value = 30 }
                ];
            },
            port: portLease.Port);

        await client.StartAsync(context => new TestRoot(context), port: portLease.Port);

        // Wait for initial sync
        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Root!.Items.Length == 3,
            timeout: TimeSpan.FromSeconds(10),
            message: "Initial collection sync");

        Assert.Equal("WithItems", client.Root!.Name);
        Assert.Equal(3, client.Root.Items.Length);
        _output.WriteLine("Initial sync verified with 3 items");

        // Restart server
        _output.WriteLine("Restarting server...");
        await server.StopAsync();
        await Task.Delay(1000);
        await server.RestartAsync();

        // Update server state after restart
        server.Root!.Name = "AfterRestart";
        server.Root.Items[0].Label = "Updated1";
        server.Root.Items[2].Value = 300;

        // Wait for client to reconnect and sync all properties
        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Root!.Name == "AfterRestart" &&
                  client.Root.Items.Length == 3 &&
                  client.Root.Items[0].Label == "Updated1" &&
                  client.Root.Items[2].Value == 300,
            timeout: TimeSpan.FromSeconds(30),
            message: "All properties should resync after restart");

        _output.WriteLine($"Client synced: Name={client.Root.Name}, Items[0].Label={client.Root.Items[0].Label}, Items[2].Value={client.Root.Items[2].Value}");
        Assert.Equal("AfterRestart", client.Root.Name);
        Assert.Equal("Updated1", client.Root.Items[0].Label);
        Assert.Equal(300, client.Root.Items[2].Value);
    }
}
