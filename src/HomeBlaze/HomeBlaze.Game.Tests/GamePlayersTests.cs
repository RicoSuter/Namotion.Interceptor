using HomeBlaze.Game.Tests.Testing;

namespace HomeBlaze.Game.Tests;

public class GamePlayersTests
{
    private readonly GameTestContext _context = new();

    [Fact]
    public void WhenAnonymousClientConnects_ThenItsPlayerSpawnsAtTheOrigin()
    {
        // Arrange
        _context.Clients.Set("tab-1", isNamed: false, isConnected: true, title: "Anonymous client (tab-…)");

        // Act
        _context.Game.ReconcilePlayers();

        // Assert
        var player = Assert.Single(_context.Game.Players).Value;
        Assert.Equal((0, 0), (player.X, player.Y));
        Assert.Equal("tab-1", player.ClientId);
        Assert.Equal("Anonymous client (tab-…)", player.Title);
        Assert.True(player.IsConnected);
    }

    [Fact]
    public void WhenAnonymousClientDisconnects_ThenItsPlayerIsRemoved()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");

        // Act
        _context.Clients.Set("tab-1", isNamed: false, isConnected: false);
        _context.Game.ReconcilePlayers();

        // Assert
        Assert.Empty(_context.Game.Players);
    }

    [Fact]
    public void WhenNamedClientDisconnects_ThenItsPlayerStaysHiddenAndDoesNotBlock()
    {
        // Arrange
        _context.Clients.Set("kitchen", isNamed: true, isConnected: true);
        _context.Game.ReconcilePlayers();

        // Act
        _context.Clients.Set("kitchen", isNamed: true, isConnected: false);
        _context.Game.ReconcilePlayers();

        // Assert
        var player = _context.Game.Players["kitchen"];
        Assert.False(player.IsConnected);
        Assert.False(player.IsBlocking);
        Assert.False(player.IsVisible);
    }

    [Fact]
    public void WhenClientIsRenamed_ThenThePlayerKeepsItsPositionAndTakesTheName()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1", x: 7, y: -3);

        // Act
        _context.Clients.Set("tab-1", isNamed: true, isConnected: true, title: "Kitchen");
        _context.Game.ReconcilePlayers();

        // Assert
        Assert.Same(player, _context.Game.Players["tab-1"]);
        Assert.Equal((7, -3), (player.X, player.Y));
        Assert.Equal("Kitchen", player.Title);
    }

    [Fact]
    public void WhenClientIsForgotten_ThenItsPlayerIsRemoved()
    {
        // Arrange
        _context.Clients.Set("kitchen", isNamed: true, isConnected: false);
        _context.Game.ReconcilePlayers();

        // Act
        _context.Clients.Remove("kitchen");
        _context.Game.ReconcilePlayers();

        // Assert
        Assert.Empty(_context.Game.Players);
    }

    [Fact]
    public void WhenTheOriginIsBlocked_ThenThePlayerSpawnsOnTheNearestFreePosition()
    {
        // Arrange
        _context.AddWall(-1, 0);

        // Act
        _context.Clients.Set("tab-1", isNamed: false, isConnected: true);
        _context.Game.ReconcilePlayers();

        // Assert
        var player = _context.Game.Players["tab-1"];
        Assert.False(player.Footprint.Intersects(new HomeBlaze.Game.World.Footprint(-1, 0, 4, 1)));
        Assert.True(Math.Abs(player.X) <= 2 && Math.Abs(player.Y) <= 2);
    }

    [Fact]
    public void WhenAPlayerReconnectsOntoBlockedTiles_ThenItMovesToTheNearestFreePosition()
    {
        // Arrange
        _context.Clients.Set("kitchen", isNamed: true, isConnected: true);
        _context.Game.ReconcilePlayers();
        _context.Clients.Set("kitchen", isNamed: true, isConnected: false);
        _context.Game.ReconcilePlayers();
        _context.AddConnectedPlayer("tab-2", x: 0, y: 0);

        // Act
        _context.Clients.Set("kitchen", isNamed: true, isConnected: true);
        _context.Game.ReconcilePlayers();

        // Assert
        var kitchen = _context.Game.Players["kitchen"];
        var other = _context.Game.Players["tab-2"];
        Assert.False(kitchen.Footprint.Intersects(other.Footprint));
    }

    [Fact]
    public void WhenThereIsNoInstallation_ThenPlayersAreKept()
    {
        // Arrange
        _context.Clients.Set("kitchen", isNamed: true, isConnected: true);
        _context.Game.ReconcilePlayers();

        // Act
        _context.Clients.Clients = null;
        _context.Game.ReconcilePlayers();

        // Assert
        Assert.Single(_context.Game.Players);
    }

    [Fact]
    public void WhenALoadedAnonymousPlayerHasNoClient_ThenItIsDropped()
    {
        // Arrange
        _context.Game.Players = new Dictionary<string, GamePlayer>
        {
            ["old-tab"] = new GamePlayer(),
            ["kitchen"] = new GamePlayer()
        };
        _context.Clients.Set("kitchen", isNamed: true, isConnected: false);

        // Act
        _context.Game.ReconcilePlayers();

        // Assert
        Assert.Equal(new[] { "kitchen" }, _context.Game.Players.Keys);
    }
}
