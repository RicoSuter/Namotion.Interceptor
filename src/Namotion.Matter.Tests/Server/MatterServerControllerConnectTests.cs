using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Namotion.Interceptor.Testing;
using Namotion.Matter.Server;

namespace Namotion.Matter.Tests.Server;

public class MatterServerControllerConnectTests
{
    [Fact]
    public async Task WhenServerSendsServerInfo_ThenConnectReturnsIt()
    {
        // Arrange
        await using var server = await FakeMatterServer.StartAsync();

        // Act
        await using var controller = await MatterServerController.ConnectAsync(server.Url);

        // Assert
        Assert.Equal(new MatterServerInfo(1, ulong.MaxValue, 15, 11, "fake/1.0", false, true, true), controller.ServerInfo);
    }

    [Fact]
    public async Task WhenServerSchemaIsOlderThanRequired_ThenConnectThrows()
    {
        // Arrange
        await using var server = await FakeMatterServer.StartAsync();
        server.ServerInfo = FakeMatterServer.CreateServerInfo(schemaVersion: MatterServerController.MinimumServerSchemaVersion - 1, minSupportedSchemaVersion: 11);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<NotSupportedException>(() => MatterServerController.ConnectAsync(server.Url));
        Assert.Contains("13", exception.Message);
    }

    [Fact]
    public async Task WhenServerRequiresNewerClient_ThenConnectThrows()
    {
        // Arrange
        await using var server = await FakeMatterServer.StartAsync();
        server.ServerInfo = FakeMatterServer.CreateServerInfo(schemaVersion: 20, minSupportedSchemaVersion: MatterServerController.ClientSchemaVersion + 1);

        // Act & Assert
        await Assert.ThrowsAsync<NotSupportedException>(() => MatterServerController.ConnectAsync(server.Url));
    }

    [Fact]
    public async Task WhenServerSendsNoServerInfo_ThenConnectTimesOut()
    {
        // Arrange
        await using var server = await FakeMatterServer.StartAsync();
        server.ServerInfo = null;

        // Act & Assert
        await Assert.ThrowsAsync<TimeoutException>(() => MatterServerController.ConnectAsync(server.Url, new MatterServerOptions { ConnectTimeout = TimeSpan.FromMilliseconds(200) }));
    }

    [Fact]
    public async Task WhenServerClosesBeforeServerInfo_ThenConnectThrowsWebSocketException()
    {
        // Arrange
        await using var server = await FakeMatterServer.StartAsync();
        server.ServerInfo = null;
        server.CloseClient();

        // Act & Assert
        await Assert.ThrowsAsync<WebSocketException>(() => MatterServerController.ConnectAsync(server.Url));
    }

    [Fact]
    public async Task WhenServerInfoIsMalformed_ThenConnectThrowsMatterException()
    {
        // Arrange
        await using var server = await FakeMatterServer.StartAsync();
        server.ServerInfo = new JsonObject { ["fabric_id"] = "not a number" };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<MatterException>(() => MatterServerController.ConnectAsync(server.Url));
        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    [Fact]
    public async Task WhenServerRefusesConnection_ThenConnectThrowsWebSocketException()
    {
        // Arrange
        var url = new Uri($"ws://127.0.0.1:{LoopbackPorts.GetFreePort()}/ws");

        // Act & Assert
        await Assert.ThrowsAsync<WebSocketException>(() => MatterServerController.ConnectAsync(url));
    }

    [Theory]
    [InlineData(nameof(MatterServerOptions.ConnectTimeout))]
    [InlineData(nameof(MatterServerOptions.RequestTimeout))]
    [InlineData(nameof(MatterServerOptions.MaxMessageSize))]
    [InlineData(nameof(MatterServerOptions.KeepAliveInterval))]
    [InlineData(nameof(MatterServerOptions.KeepAliveTimeout))]
    public async Task WhenOptionIsNotPositive_ThenConnectThrowsArgumentOutOfRangeException(string option)
    {
        // Arrange
        var options = option switch
        {
            nameof(MatterServerOptions.ConnectTimeout) => new MatterServerOptions { ConnectTimeout = TimeSpan.Zero },
            nameof(MatterServerOptions.RequestTimeout) => new MatterServerOptions { RequestTimeout = TimeSpan.Zero },
            nameof(MatterServerOptions.MaxMessageSize) => new MatterServerOptions { MaxMessageSize = 0 },
            nameof(MatterServerOptions.KeepAliveInterval) => new MatterServerOptions { KeepAliveInterval = TimeSpan.Zero },
            _ => new MatterServerOptions { KeepAliveTimeout = TimeSpan.FromSeconds(-1) }
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => MatterServerController.ConnectAsync(new Uri("ws://127.0.0.1:1/ws"), options));
        Assert.Contains(option, exception.ParamName);
    }

    [Fact]
    public async Task WhenConnectingOverUnixSocket_ThenServerInfoIsReceived()
    {
        // Arrange
        var socketPath = Path.Combine(Path.GetTempPath(), $"matter-{Guid.NewGuid():N}.sock");
        await using var server = await FakeMatterServer.StartAsync(socketPath);

        // Act
        await using var controller = await MatterServerController.ConnectAsync(server.Url);

        // Assert
        Assert.Equal("unix", server.Url.Scheme);
        Assert.Equal(15, controller.ServerInfo.SchemaVersion);
    }

    [Fact]
    public async Task WhenUrlSchemeIsUnsupported_ThenConnectThrows()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => MatterServerController.ConnectAsync(new Uri("http://localhost:5580/ws")));
    }

    [Fact]
    public async Task WhenDisposedTwice_ThenSecondDisposeCompletes()
    {
        // Arrange
        await using var server = await FakeMatterServer.StartAsync();
        var controller = await MatterServerController.ConnectAsync(server.Url);

        // Act
        await Task.WhenAll(DisposeAsTask(controller), DisposeAsTask(controller)).WaitAsync(TestTimeouts.Default);

        // Assert
        Assert.True(controller.Events.Completion.IsCompletedSuccessfully);
    }

    private static Task DisposeAsTask(IAsyncDisposable disposable) => disposable.DisposeAsync().AsTask();
}
