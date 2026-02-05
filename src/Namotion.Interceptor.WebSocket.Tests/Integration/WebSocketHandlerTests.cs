using System;
using System.Threading.Tasks;
using Namotion.Interceptor.Testing;
using Xunit;
using Xunit.Abstractions;

namespace Namotion.Interceptor.WebSocket.Tests.Integration;

/// <summary>
/// Integration tests for AddWebSocketSubjectHandler and MapWebSocketSubjectHandler
/// which embed WebSocket handling in an existing ASP.NET Core application.
/// </summary>
[Trait("Category", "Integration")]
public class WebSocketHandlerTests
{
    private readonly ITestOutputHelper _output;

    public WebSocketHandlerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task EmbeddedServer_ServerWriteProperty_ShouldUpdateClient()
    {
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketEmbeddedTestServer<TestRoot>(_output);
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
    public async Task EmbeddedServer_ClientWriteProperty_ShouldUpdateServer()
    {
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketEmbeddedTestServer<TestRoot>(_output);
        await using var client = new WebSocketTestClient<TestRoot>(_output);

        await server.StartAsync(context => new TestRoot(context), port: portLease.Port);
        await client.StartAsync(context => new TestRoot(context), port: portLease.Port);

        // Client updates property
        client.Root!.Name = "Updated from Client";
        await Task.Delay(500);

        Assert.Equal("Updated from Client", server.Root!.Name);
    }

    [Fact]
    public async Task EmbeddedServer_NumericProperty_ShouldSyncBidirectionally()
    {
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketEmbeddedTestServer<TestRoot>(_output);
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
    public async Task EmbeddedServer_MultipleClients_ShouldAllReceiveUpdates()
    {
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketEmbeddedTestServer<TestRoot>(_output);
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
    public async Task EmbeddedServer_WithCollectionItems_ShouldSyncCollection()
    {
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketEmbeddedTestServer<TestRoot>(_output);
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
                    new TestItem(context) { Label = "Item2", Value = 20 }
                ];
            },
            port: portLease.Port);

        await client.StartAsync(context => new TestRoot(context), port: portLease.Port);

        // Wait for initial sync
        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Root!.Items.Length == 2,
            timeout: TimeSpan.FromSeconds(10),
            message: "Collection should sync");

        Assert.Equal("WithItems", client.Root!.Name);
        Assert.Equal(2, client.Root.Items.Length);
        Assert.Equal("Item1", client.Root.Items[0].Label);
        Assert.Equal(20, client.Root.Items[1].Value);
    }

    [Fact]
    public async Task EmbeddedServer_Restart_ClientRecovers()
    {
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketEmbeddedTestServer<TestRoot>(_output);
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

        // Restart server
        _output.WriteLine("Restarting embedded server...");
        await server.StopAsync();
        await Task.Delay(1000);
        await server.RestartAsync();

        // Update server state after restart
        server.Root!.Name = "AfterRestart";

        // Wait for client to reconnect and sync
        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Root!.Name == "AfterRestart",
            timeout: TimeSpan.FromSeconds(30),
            message: "Client should receive update after embedded server restart");

        _output.WriteLine($"Client received: {client.Root.Name}");
        Assert.Equal("AfterRestart", client.Root.Name);
    }

    [Fact]
    public async Task EmbeddedServer_Handler_IsAccessible()
    {
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketEmbeddedTestServer<TestRoot>(_output);

        await server.StartAsync(context => new TestRoot(context), port: portLease.Port);

        // Verify handler is accessible
        Assert.NotNull(server.Handler);
    }
}
