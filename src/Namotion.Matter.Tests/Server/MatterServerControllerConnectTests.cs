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
        server.ServerInfo = FakeMatterServer.CreateServerInfo(schemaVersion: 13, minSupportedSchemaVersion: 11);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<NotSupportedException>(() => MatterServerController.ConnectAsync(server.Url));
        Assert.Contains("13", exception.Message);
    }

    [Fact]
    public async Task WhenServerRequiresNewerClient_ThenConnectThrows()
    {
        // Arrange
        await using var server = await FakeMatterServer.StartAsync();
        server.ServerInfo = FakeMatterServer.CreateServerInfo(schemaVersion: 20, minSupportedSchemaVersion: 16);

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
        await Assert.ThrowsAsync<TimeoutException>(() => MatterServerController.ConnectAsync(server.Url, new MatterServerOptions { RequestTimeout = TimeSpan.FromMilliseconds(200) }));
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
}
