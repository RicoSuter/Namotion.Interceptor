using HomeBlaze.Abstractions.Messaging;
using HomeBlaze.Services.Clients;

namespace HomeBlaze.Services.Tests.Clients;

public class ClientConnectionServiceTests
{
    [Fact]
    public void WhenACircuitConnectsAndDisconnects_ThenTheSnapshotAndChangedFollow()
    {
        // Arrange
        var service = new ClientConnectionService(TimeProvider.System);
        var changes = 0;
        service.Changed += () => changes++;

        // Act
        var connection = service.Connect("cookie", new NoCallbacks());
        var connected = service.Connections;
        service.Disconnect(connection);

        // Assert
        Assert.Equal(connection, Assert.Single(connected));
        Assert.Equal("cookie", connection.CookieClientId);
        Assert.Equal(12, connection.ConnectionId.Length);
        Assert.Empty(service.Connections);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void WhenIdsAreCreated_ThenTheyAreLowercaseBase32AndDistinct()
    {
        // Act
        var first = ClientConnectionService.CreateClientId();
        var second = ClientConnectionService.CreateClientId();

        // Assert
        Assert.Matches("^[a-z2-7]{12}$", first);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void WhenAConnectionIsAssigned_ThenItReportsTheClient()
    {
        // Arrange
        var connection = new ClientConnectionService(TimeProvider.System).Connect(null, new NoCallbacks());

        // Act
        connection.Assign("abc", "Kitchen");

        // Assert
        Assert.Equal("abc", connection.ClientId);
        Assert.Equal("Kitchen", connection.ClientName);
    }

    [Fact]
    public async Task WhenNoRenameHandlerIsSet_ThenRenamingThrows()
    {
        // Arrange
        var service = new ClientConnectionService(TimeProvider.System);
        var connection = service.Connect(null, new NoCallbacks());

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RenameAsync(connection, "Kitchen", CancellationToken.None));
    }

    [Fact]
    public async Task WhenARenameHandlerIsSetAndDisposed_ThenRenamingCallsItOnlyWhileSet()
    {
        // Arrange
        var service = new ClientConnectionService(TimeProvider.System);
        var connection = service.Connect(null, new NoCallbacks());
        string? renamed = null;
        var registration = service.SetRenameHandler((_, name, _) =>
        {
            renamed = name;
            return Task.CompletedTask;
        });

        // Act
        await service.RenameAsync(connection, "Kitchen", CancellationToken.None);
        registration.Dispose();

        // Assert
        Assert.Equal("Kitchen", renamed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RenameAsync(connection, "Hall", CancellationToken.None));
    }

    private sealed class NoCallbacks : IClientConnectionCallbacks
    {
        public Task ShowNotificationAsync(Notification notification, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StoreClientIdAsync(string clientId) => Task.CompletedTask;
    }
}
