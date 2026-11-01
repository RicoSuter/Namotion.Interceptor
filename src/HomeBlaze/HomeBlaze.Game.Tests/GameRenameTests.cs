using HomeBlaze.Game.Clients;
using HomeBlaze.Game.Tests.Testing;

namespace HomeBlaze.Game.Tests;

public class GameRenameTests
{
    private readonly GameTestContext _context = new();

    [Fact]
    public async Task WhenAPlayerChangesItsName_ThenItsClientIsNamedAndThePlayerTakesTheName()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1", x: 7, y: -3);
        var caller = new GameCaller(player.ClientId);

        // Act
        await _context.Game.RenameAsync(caller, "  Kitchen  ", CancellationToken.None);
        _context.Game.ReconcilePlayers();

        // Assert
        Assert.Equal(new GameClient("tab-1", "Kitchen", IsNamed: true, IsConnected: true), GetClient("tab-1"));
        Assert.Same(player, _context.Game.Players["tab-1"]);
        Assert.Equal("Kitchen", player.ClientTitle);
        Assert.Equal((7, -3), (player.X, player.Y));
        Assert.Equal("Kitchen", _context.Game.GetClientName(caller));
    }

    [Fact]
    public void WhenTheClientIsAnonymous_ThenThePlayerHasNoClientName()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");

        // Act
        var name = _context.Game.GetClientName(new GameCaller(player.ClientId));

        // Assert
        Assert.Null(name);
    }

    [Fact]
    public async Task WhenTheCallerIsNotAPlayerOfTheGame_ThenRenamingIsRefused()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        var stranger = new GameCaller("tab-2");

        // Act & Assert
        var withoutCaller = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _context.Game.RenameAsync(null, "Kitchen", CancellationToken.None));
        var withStranger = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _context.Game.RenameAsync(stranger, "Kitchen", CancellationToken.None));
        Assert.Equal(Game.NoRenameActorMessage, withoutCaller.Message);
        Assert.Equal(Game.NoRenameActorMessage, withStranger.Message);
        Assert.False(GetClient("tab-1").IsNamed);
        Assert.Null(_context.Game.GetClientName(null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task WhenTheNameIsEmpty_ThenRenamingIsRefused(string name)
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => _context.Game.RenameAsync(new GameCaller(player.ClientId), name, CancellationToken.None));
        Assert.False(GetClient("tab-1").IsNamed);
    }

    [Fact]
    public async Task WhenTheNameIsTooLong_ThenRenamingIsRefused()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var name = new string('a', Game.MaximumNameLength + 1);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => _context.Game.RenameAsync(new GameCaller(player.ClientId), name, CancellationToken.None));
        Assert.False(GetClient("tab-1").IsNamed);
    }

    [Fact]
    public async Task WhenTheNameHasTheMostCharactersAfterTrimming_ThenItIsTaken()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var name = new string('a', Game.MaximumNameLength);

        // Act
        await _context.Game.RenameAsync(new GameCaller(player.ClientId), $" {name} ", CancellationToken.None);

        // Assert
        Assert.Equal(name, GetClient("tab-1").Title);
    }

    [Fact]
    public async Task WhenAPlayerChangesItsName_ThenTheOtherClientsKeepTheirNames()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        _context.Clients.Set("kitchen", isNamed: true, isConnected: true, title: "Kitchen");
        _context.Game.ReconcilePlayers();
        var kitchen = _context.Game.Players["kitchen"];

        // Act
        await _context.Game.RenameAsync(new GameCaller(player.ClientId), "Office", CancellationToken.None);
        _context.Game.ReconcilePlayers();

        // Assert
        Assert.Equal(new GameClient("kitchen", "Kitchen", IsNamed: true, IsConnected: true), GetClient("kitchen"));
        Assert.Equal("Kitchen", kitchen.ClientTitle);
        Assert.Equal("Office", player.ClientTitle);
    }

    private GameClient GetClient(string id) =>
        Assert.Single(_context.Clients.GetClients() ?? [], client => client.Id == id);
}
