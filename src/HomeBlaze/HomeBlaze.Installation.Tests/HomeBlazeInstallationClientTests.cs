using HomeBlaze.Abstractions.Messaging;
using HomeBlaze.Installation.Tests.Testing;
using HomeBlaze.Services;
using HomeBlaze.Services.Clients;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor.Testing;
using Xunit;

namespace HomeBlaze.Installation.Tests;

public class HomeBlazeInstallationClientTests
{
    private readonly ClientConnectionService _connections = new(TimeProvider.System);
    private readonly TestRoot _root;
    private readonly HomeBlazeInstallation _installation;

    public HomeBlazeInstallationClientTests()
    {
        _root = new TestRoot(SubjectContextFactory.Create(new ServiceCollection()));
        _installation = HomeBlazeInstallationTests.CreateInstallation(HomeBlazeInstallationTests.CreateBuild(), clientConnections: _connections);
        _root.Installation = _installation;
    }

    [Fact]
    public void WhenAnAnonymousCircuitConnectsAndCloses_ThenItsClientComesAndGoes()
    {
        // Arrange
        var connection = _connections.Connect(null, new FakeClientCallbacks());

        // Act
        _installation.ReconcileClients();
        var client = Assert.Single(_installation.Clients).Value;
        _connections.Disconnect(connection);
        _installation.ReconcileClients();

        // Assert
        Assert.Equal(connection.ConnectionId, client.Id);
        Assert.Equal(connection.ConnectionId, connection.ClientId);
        Assert.False(client.IsNamed);
        Assert.StartsWith("Anonymous client (", client.Title);
        Assert.Empty(_installation.Clients);
        Assert.Equal(0, _installation.ConnectedCircuitCount);
    }

    [Fact]
    public async Task WhenAClientIsRenamed_ThenItIsPersistedAndItsTabStoresTheId()
    {
        // Arrange
        var callbacks = new FakeClientCallbacks();
        var connection = _connections.Connect(null, callbacks);
        _installation.ReconcileClients();
        var client = _installation.Clients[connection.ConnectionId];

        // Act
        await client.RenameAsync("  Kitchen tablet ", CancellationToken.None);

        // Assert
        Assert.Equal("Kitchen tablet", client.Name);
        Assert.Equal("Kitchen tablet", connection.ClientName);
        Assert.Equal(connection.ConnectionId, connection.CookieClientId);
        Assert.Contains(_installation, _root.Written);
        await AsyncTestHelpers.WaitUntilAsync(() => callbacks.StoredClientIds.Count == 1, message: "The id was not stored");
        Assert.Equal(connection.ConnectionId, callbacks.StoredClientIds[0]);
    }

    [Fact]
    public async Task WhenANamedClientCloses_ThenItStaysOfflineAndALaterTabWithItsCookieJoinsIt()
    {
        // Arrange
        var first = _connections.Connect(null, new FakeClientCallbacks());
        _installation.ReconcileClients();
        await _installation.Clients[first.ConnectionId].RenameAsync("Hall", CancellationToken.None);
        var id = first.ConnectionId;

        // Act
        _connections.Disconnect(first);
        _installation.ReconcileClients();
        var offline = _installation.Clients[id];
        var isConnectedAfterClose = offline.IsConnected;
        var second = _connections.Connect(id, new FakeClientCallbacks());
        var third = _connections.Connect(id, new FakeClientCallbacks());
        _installation.ReconcileClients();

        // Assert
        Assert.False(isConnectedAfterClose);
        var client = Assert.Single(_installation.Clients).Value;
        Assert.Same(offline, client);
        Assert.True(client.IsConnected);
        Assert.Equal(2, client.ConnectionCount);
        Assert.Equal(id, second.ClientId);
        Assert.Equal(id, third.ClientId);
        Assert.Equal(2, _installation.ConnectedCircuitCount);
    }

    [Fact]
    public void WhenTheCookieNamesNoKnownClient_ThenTheTabIsAnonymous()
    {
        // Arrange
        var connection = _connections.Connect("forgotten123", new FakeClientCallbacks());

        // Act
        _installation.ReconcileClients();

        // Assert
        Assert.Equal(connection.ConnectionId, Assert.Single(_installation.Clients).Key);
    }

    [Fact]
    public async Task WhenANamedClientIsForgotten_ThenItIsRemovedAndItsTabIsAnonymousAgain()
    {
        // Arrange
        var connection = _connections.Connect(null, new FakeClientCallbacks());
        _installation.ReconcileClients();
        var id = connection.ConnectionId;
        await _installation.Clients[id].RenameAsync("Hall", CancellationToken.None);
        _connections.Disconnect(connection);
        _installation.ReconcileClients();

        // Act
        await _installation.Clients[id].ForgetAsync(CancellationToken.None);

        // Assert
        Assert.Empty(_installation.Clients);
    }

    [Fact]
    public async Task WhenTheConfigurationIsApplied_ThenUnnamedClientsWithoutCircuitAreDropped()
    {
        // Arrange
        var loaded = new Dictionary<string, HomeBlazeClient>
        {
            ["named1234567"] = new() { Name = "Hall" },
            ["anonymous123"] = new()
        };

        // Act
        _installation.Clients = loaded;
        await _installation.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        var client = Assert.Single(_installation.Clients);
        Assert.Equal("named1234567", client.Key);
        Assert.Equal("named1234567", client.Value.Id);
        Assert.False(client.Value.IsConnected);
    }

    [Fact]
    public async Task WhenNotifyingAnOfflineClient_ThenTheSendFails()
    {
        // Arrange
        var connection = _connections.Connect(null, new FakeClientCallbacks());
        _installation.ReconcileClients();
        var client = _installation.Clients[connection.ConnectionId];
        await client.RenameAsync("Hall", CancellationToken.None);
        _connections.Disconnect(connection);
        _installation.ReconcileClients();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.SendNotificationAsync(new Notification { Message = "Hello" }, CancellationToken.None));
        Assert.Contains("not connected", exception.Message);
    }

    [Fact]
    public async Task WhenNotifyingAClientWithTwoTabsOfWhichOneFails_ThenTheOtherShowsItAndTheSendSucceeds()
    {
        // Arrange
        var first = _connections.Connect(null, new FakeClientCallbacks());
        _installation.ReconcileClients();
        var client = _installation.Clients[first.ConnectionId];
        await client.RenameAsync("Hall", CancellationToken.None);
        var working = new FakeClientCallbacks();
        _connections.Connect(first.ConnectionId, working);
        _connections.Disconnect(first);
        _connections.Connect(first.ConnectionId, new FakeClientCallbacks { FailsToShow = true });
        _installation.ReconcileClients();

        // Act
        var result = await client.SendNotificationAsync(new Notification { Message = "Hello" }, CancellationToken.None);

        // Assert
        Assert.NotNull(result.Id);
        Assert.Equal("Hello", Assert.Single(working.Notifications).Message);
    }

    [Fact]
    public async Task WhenRunning_ThenACircuitIsReflectedWithoutReconcilingByHand()
    {
        // Arrange
        await _installation.StartAsync(CancellationToken.None);
        try
        {
            // Act
            _connections.Connect(null, new FakeClientCallbacks());

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => _installation.ConnectedCircuitCount == 1 && _installation.Clients.Count == 1,
                message: "The circuit was not reflected");
            Assert.Single(_installation.Clients);
        }
        finally
        {
            await _installation.StopAsync(CancellationToken.None);
        }
    }
}
