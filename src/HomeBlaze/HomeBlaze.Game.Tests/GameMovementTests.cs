using HomeBlaze.Game.Tests.Testing;

namespace HomeBlaze.Game.Tests;

public class GameMovementTests
{
    private readonly GameTestContext _context = new();

    private MoveResult Step(GamePlayer player, GameDirection direction, bool turnOnly = false)
    {
        _context.TimeProvider.Advance(Game.StepInterval);
        return _context.Game.TryMove(player, direction, turnOnly);
    }

    [Fact]
    public void WhenThePathIsFree_ThenTheStepMovesOneTileAndTurns()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");

        // Act
        var result = Step(player, GameDirection.East);

        // Assert
        Assert.Equal(new MoveResult(true, 1, 0, GameDirection.East), result);
        Assert.Equal((1, 0, GameDirection.East), (player.X, player.Y, player.Facing));
    }

    [Fact]
    public void WhenAWallBlocksOneTileOfTheFootprint_ThenTheStepIsRefusedButThePlayerTurns()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        _context.AddWall(1, 2, width: 1, depth: 1);

        // Act
        var result = Step(player, GameDirection.North);

        // Assert
        Assert.False(result.IsAccepted);
        Assert.Equal((0, 0, GameDirection.North), (player.X, player.Y, player.Facing));
    }

    [Fact]
    public void WhenTurningOnly_ThenThePositionStays()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");

        // Act
        var result = Step(player, GameDirection.West, turnOnly: true);

        // Assert
        Assert.Equal(new MoveResult(true, 0, 0, GameDirection.West), result);
    }

    [Fact]
    public void WhenADiagonalStepSqueezesBetweenCornerBlockers_ThenItIsRefused()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        _context.AddWall(2, 0, width: 1, depth: 1);
        _context.AddWall(0, 2, width: 1, depth: 1);

        // Act
        var result = Step(player, GameDirection.NorthEast);

        // Assert
        Assert.False(result.IsAccepted);
        Assert.Equal((0, 0), (player.X, player.Y));
    }

    [Fact]
    public void WhenADiagonalStepHasOneOrthogonalWayFree_ThenItIsAccepted()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        _context.AddWall(2, -1, width: 1, depth: 2);

        // Act
        var result = Step(player, GameDirection.NorthEast);

        // Assert
        Assert.True(result.IsAccepted);
        Assert.Equal((1, 1), (player.X, player.Y));
    }

    [Fact]
    public void WhenFourStepsComeWithinOneInterval_ThenTheFourthIsRefused()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");

        // Act
        var results = Enumerable.Range(0, 4).Select(_ => _context.Game.TryMove(player, GameDirection.East, turnOnly: false).IsAccepted).ToArray();

        // Assert
        Assert.Equal(new[] { true, true, true, false }, results);
        Assert.Equal(3, player.X);
    }

    [Fact]
    public void WhenAStepWouldLeaveTheIntRange_ThenItIsRefused()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1", x: int.MaxValue - 1);

        // Act
        var result = Step(player, GameDirection.East);

        // Assert
        Assert.False(result.IsAccepted);
        Assert.Equal(int.MaxValue - 1, player.X);
    }

    [Fact]
    public void WhenAnOfflinePlayerStandsInTheWay_ThenTheStepIsAccepted()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        _context.Clients.Set("kitchen", isNamed: true, isConnected: true);
        _context.Game.ReconcilePlayers();
        var offline = _context.Game.Players["kitchen"];
        offline.X = 2;
        offline.Y = 0;
        _context.Clients.Set("kitchen", isNamed: true, isConnected: false);
        _context.Game.ReconcilePlayers();

        // Act
        var result = Step(player, GameDirection.East);

        // Assert
        Assert.True(result.IsAccepted);
    }

    [Fact]
    public void WhenThePlayerIsNotPartOfTheGame_ThenTheStepIsRefused()
    {
        // Arrange
        var stranger = new GamePlayer { IsConnected = true };

        // Act
        var result = Step(stranger, GameDirection.East);

        // Assert
        Assert.False(result.IsAccepted);
        Assert.Equal(0, stranger.X);
    }

    [Fact]
    public void WhenAWallStandsInFront_ThenItIsATargetObject()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var wall = _context.AddWall(-1, 2);

        // Act
        var targets = _context.Game.GetTargetObjects(player);

        // Assert
        Assert.Same(wall, Assert.Single(targets));
    }
}
