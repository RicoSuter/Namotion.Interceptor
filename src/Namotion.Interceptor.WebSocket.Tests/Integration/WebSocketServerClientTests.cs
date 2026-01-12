using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Namotion.Interceptor.WebSocket.Tests.Integration;

[Collection("WebSocket Integration")]
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
        await using var server = new WebSocketTestServer<TestRoot>(_output);
        await using var client = new WebSocketTestClient<TestRoot>(_output);

        await server.StartAsync(
            context => new TestRoot(context),
            (_, root) => root.Name = "Initial");

        await client.StartAsync(context => new TestRoot(context));

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
        await using var server = new WebSocketTestServer<TestRoot>(_output);
        await using var client = new WebSocketTestClient<TestRoot>(_output);

        await server.StartAsync(context => new TestRoot(context));
        await client.StartAsync(context => new TestRoot(context));

        // Client updates property
        client.Root!.Name = "Updated from Client";
        await Task.Delay(500);

        Assert.Equal("Updated from Client", server.Root!.Name);
    }

    [Fact]
    public async Task NumericProperty_ShouldSyncBidirectionally()
    {
        await using var server = new WebSocketTestServer<TestRoot>(_output);
        await using var client = new WebSocketTestClient<TestRoot>(_output);

        await server.StartAsync(context => new TestRoot(context));
        await client.StartAsync(context => new TestRoot(context));

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
        await using var server = new WebSocketTestServer<TestRoot>(_output);
        await using var client1 = new WebSocketTestClient<TestRoot>(_output);
        await using var client2 = new WebSocketTestClient<TestRoot>(_output);

        await server.StartAsync(context => new TestRoot(context));
        await client1.StartAsync(context => new TestRoot(context));
        await client2.StartAsync(context => new TestRoot(context));

        server.Root!.Name = "Broadcast Test";
        await Task.Delay(500);

        Assert.Equal("Broadcast Test", client1.Root!.Name);
        Assert.Equal("Broadcast Test", client2.Root!.Name);
    }
}
