using HomeBlaze.Game.Tests.Testing;

namespace HomeBlaze.Game.Tests;

public class GameActionsTests
{
    private readonly GameTestContext _context = new();

    [Fact]
    public async Task WhenAPlayerBuildsAWall_ThenItStandsInFrontAndIsWritten()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");

        // Act
        await _context.Game.BuildWallAsync(new GameCaller(player), CancellationToken.None);

        // Assert
        var wall = Assert.IsType<Wall>(Assert.Single(_context.Game.Objects).Value);
        Assert.Equal((-1, 2, 4, 1, 10), (wall.X, wall.Y, wall.Width, wall.Depth, wall.Height));
        Assert.Equal(1, _context.Root.WriteCount);
        Assert.Same(wall, Assert.Single(_context.Game.GetTargetObjects(player)));
    }

    [Fact]
    public async Task WhenObjectsWereReplacedFromOutside_ThenBuildingAWallKeepsThem()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var existing = new Wall { X = 20, Y = 20 };
        _context.Game.Objects = new Dictionary<string, GameObject> { ["loaded"] = existing };

        // Act
        await _context.Game.BuildWallAsync(new GameCaller(player), CancellationToken.None);

        // Assert
        Assert.Equal(2, _context.Game.Objects.Count);
        Assert.Same(existing, _context.Game.Objects["loaded"]);
    }

    [Fact]
    public async Task WhenSomethingIsInTheWay_ThenBuildingIsRefused()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        _context.AddWall(1, 2, width: 1, depth: 1);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => _context.Game.BuildWallAsync(new GameCaller(player), CancellationToken.None));
        Assert.Single(_context.Game.Objects);
    }

    [Fact]
    public async Task WhenThePlayerFacesDiagonally_ThenBuildingIsRefused()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        _context.Game.TryMove(player, GameDirection.NorthEast, turnOnly: true);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => _context.Game.BuildWallAsync(new GameCaller(player), CancellationToken.None));
    }

    [Fact]
    public async Task WhenThereIsNoActor_ThenBuildingIsRefused()
    {
        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => _context.Game.BuildWallAsync(null, CancellationToken.None));
    }

    [Fact]
    public void WhenTheCallerIsAPlayerOfAnotherGame_ThenThereIsNoActor()
    {
        // Arrange
        var stranger = new GamePlayer { IsConnected = true };

        // Act
        var actor = _context.Game.ResolveActor(new GameCaller(stranger));

        // Assert
        Assert.Null(actor);
    }

    [Fact]
    public async Task WhenAWallIsDemolished_ThenItIsRemovedAndWritten()
    {
        // Arrange
        var wall = _context.AddWall(5, 5);

        // Act
        await wall.DemolishAsync(CancellationToken.None);

        // Assert
        Assert.Empty(_context.Game.Objects);
        Assert.Equal(1, _context.Root.WriteCount);
    }

    [Fact]
    public async Task WhenAPlayerSendsAMessage_ThenTheOtherClientGetsItWithTheSender()
    {
        // Arrange
        var sender = _context.AddConnectedPlayer("tab-1");
        _context.Clients.Set("tab-1", isNamed: true, isConnected: true, title: "Kitchen");
        _context.Game.ReconcilePlayers();
        var recipient = _context.AddConnectedPlayer("tab-2", x: 0, y: 2);

        // Act
        await recipient.SendMessageAsync("  Hello  ", new GameCaller(sender), CancellationToken.None);

        // Assert
        var (clientId, notification) = Assert.Single(_context.Clients.Notifications);
        Assert.Equal("tab-2", clientId);
        Assert.Equal("Message from Kitchen", notification.Title);
        Assert.Equal("Hello", notification.Message);
    }
}
