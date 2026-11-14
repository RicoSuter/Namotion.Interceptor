using HomeBlaze.Abstractions.Messaging;
using HomeBlaze.Services.Clients;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeBlaze.Services.Tests.Clients;

public class ClientConnectionServiceTests
{
    [Fact]
    public void WhenACircuitConnectsAndDisconnects_ThenTheSnapshotAndChangedFollow()
    {
        // Arrange
        var service = CreateService();
        var changes = 0;
        service.Changed += () => changes++;

        // Act
        var connection = service.Connect(null, new NoCallbacks());
        var connected = service.Connections;
        service.Disconnect(connection);

        // Assert
        Assert.Equal(connection, Assert.Single(connected));
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
    public void WhenACircuitConnectsWithAToken_ThenItKeepsTheHashOfTheTokenAndNotTheToken()
    {
        // Arrange
        var service = CreateService();
        var token = ClientConnectionService.CreateClientToken();

        // Act
        var connection = service.Connect(token, new NoCallbacks());

        // Assert
        Assert.Equal(ClientConnectionService.HashClientToken(token), connection.CookieTokenHash);
        Assert.NotEqual(token, connection.CookieTokenHash);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void WhenACircuitConnectsWithoutAToken_ThenItHasNoTokenHash(string? token)
    {
        // Act
        var connection = CreateService().Connect(token, new NoCallbacks());

        // Assert
        Assert.Null(connection.CookieTokenHash);
    }

    [Fact]
    public void WhenTokensAreCreated_ThenTheyAre43Base64UrlCharactersAndDistinct()
    {
        // Act
        var first = ClientConnectionService.CreateClientToken();
        var second = ClientConnectionService.CreateClientToken();

        // Assert
        Assert.Matches("^[A-Za-z0-9_-]{43}$", first);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", second);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void WhenATokenIsHashed_ThenTheHashIsDeterministicLowercaseHexOf64Characters()
    {
        // Act
        var first = ClientConnectionService.HashClientToken("token");
        var second = ClientConnectionService.HashClientToken("token");
        var other = ClientConnectionService.HashClientToken("other");

        // Assert
        Assert.Matches("^[0-9a-f]{64}$", first);
        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
        Assert.Equal("3c469e9d6c5875d37a43f353d4f88e61fcf812c66eee3457465a40b0da4153e0", first);
    }

    [Fact]
    public void WhenAConnectionIsAssigned_ThenItReportsTheClient()
    {
        // Arrange
        var connection = CreateService().Connect(null, new NoCallbacks());

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
        var service = CreateService();
        var connection = service.Connect(null, new NoCallbacks());

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RenameAsync(connection, "Kitchen", CancellationToken.None));
    }

    [Fact]
    public async Task WhenARenameHandlerIsSetAndDisposed_ThenRenamingCallsItOnlyWhileSet()
    {
        // Arrange
        var service = CreateService();
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

    [Fact]
    public void WhenAChangedSubscriberThrows_ThenConnectAndDisconnectCompleteAndTheOtherSubscribersRun()
    {
        // Arrange
        var service = CreateService();
        var changes = 0;
        service.Changed += () => throw new InvalidOperationException("The subscriber failed.");
        service.Changed += () => changes++;

        // Act
        var connection = service.Connect(null, new NoCallbacks());
        var connected = service.Connections;
        service.Disconnect(connection);

        // Assert
        Assert.Equal(connection, Assert.Single(connected));
        Assert.Empty(service.Connections);
        Assert.Equal(2, changes);
    }

    private static ClientConnectionService CreateService() =>
        new(TimeProvider.System, NullLogger<ClientConnectionService>.Instance);

    private sealed class NoCallbacks : IClientConnectionCallbacks
    {
        public Task ShowNotificationAsync(Notification notification, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StoreClientTokenAsync(string token) => Task.CompletedTask;
    }
}
