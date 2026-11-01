using HomeBlaze.Game.Tests.Testing;

namespace HomeBlaze.Game.Tests;

public class GameMovementTests
{
    private readonly GameTestContext _context = new();

    // Steps and publishes, so the subject shows the result.
    private async Task<MoveResult?> Step(GamePlayer player, GameDirection direction, bool turnOnly = false)
    {
        var result = await _context.StepAsync(player.ClientId, direction, turnOnly);
        _context.Publish();
        return result;
    }

    private IReadOnlyList<GameObject> GetTargets(GamePlayer player) =>
        _context.GetTargets(player.ClientId);

    [Fact]
    public async Task WhenThePathIsFree_ThenTheStepMovesOneTileAndTurns()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");

        // Act
        var result = await Step(player, GameDirection.East);

        // Assert
        Assert.Equal(new MoveResult(true, 1, 0, GameDirection.East), result);
        Assert.Equal((1, 0, GameDirection.East), (player.X, player.Y, player.Facing));
    }

    [Fact]
    public async Task WhenAWallBlocksOneTileOfTheFootprint_ThenTheStepIsRefusedButThePlayerTurns()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        _context.AddWall(1, 2, width: 1, depth: 1);

        // Act
        var result = await Step(player, GameDirection.North);

        // Assert
        Assert.False(result!.Value.IsAccepted);
        Assert.Equal((0, 0, GameDirection.North), (player.X, player.Y, player.Facing));
    }

    [Fact]
    public async Task WhenTurningOnly_ThenThePositionStays()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");

        // Act
        var result = await Step(player, GameDirection.West, turnOnly: true);

        // Assert
        Assert.Equal(new MoveResult(true, 0, 0, GameDirection.West), result);
    }

    [Fact]
    public async Task WhenADiagonalStepSqueezesBetweenCornerBlockers_ThenItIsRefused()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        _context.AddWall(2, 0, width: 1, depth: 1);
        _context.AddWall(0, 2, width: 1, depth: 1);

        // Act
        var result = await Step(player, GameDirection.NorthEast);

        // Assert
        Assert.False(result!.Value.IsAccepted);
        Assert.Equal((0, 0), (player.X, player.Y));
    }

    [Fact]
    public async Task WhenADiagonalStepHasOneOrthogonalWayFree_ThenItIsAccepted()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        _context.AddWall(2, -1, width: 1, depth: 2);

        // Act
        var result = await Step(player, GameDirection.NorthEast);

        // Assert
        Assert.True(result!.Value.IsAccepted);
        Assert.Equal((1, 1), (player.X, player.Y));
    }

    [Fact]
    public async Task WhenFourStepsComeWithinOneInterval_ThenTheFourthIsRefused()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");

        // Act
        var steps = Enumerable.Range(0, 4).Select(_ => _context.Game.StepAsync(player.ClientId, GameDirection.East, turnOnly: false)).ToArray();
        _context.Tick();
        var results = (await Task.WhenAll(steps)).Select(result => result!.Value.IsAccepted).ToArray();
        _context.Publish();

        // Assert
        Assert.Equal(new[] { true, true, true, false }, results);
        Assert.Equal(3, player.X);
    }

    [Fact]
    public async Task WhenFourTurnsComeWithinOneInterval_ThenTheFourthIsRefused()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var directions = new[] { GameDirection.East, GameDirection.South, GameDirection.West, GameDirection.North };

        // Act
        var turns = directions.Select(direction => _context.Game.StepAsync(player.ClientId, direction, turnOnly: true)).ToArray();
        _context.Tick();
        var results = (await Task.WhenAll(turns)).Select(result => result!.Value.IsAccepted).ToArray();
        _context.Publish();

        // Assert
        Assert.Equal(new[] { true, true, true, false }, results);
        Assert.Equal(GameDirection.West, player.Facing);
    }

    [Fact]
    public async Task WhenABlockedStepKeepsTheFacing_ThenNoSaveIsRequested()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        _context.AddWall(0, 2, width: 2, depth: 1);
        await _context.SaveAsync();
        var writesBefore = _context.Root.WriteCount;

        // Act
        var result = await Step(player, GameDirection.North);
        await _context.SaveAsync();

        // Assert
        Assert.False(result!.Value.IsAccepted);
        Assert.Equal(writesBefore, _context.Root.WriteCount);
    }

    [Fact]
    public async Task WhenAStepWouldLeaveTheIntRange_ThenItIsRefused()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1", x: int.MaxValue - 1);

        // Act
        var result = await Step(player, GameDirection.East);

        // Assert
        Assert.False(result!.Value.IsAccepted);
        Assert.Equal(int.MaxValue - 1, player.X);
    }

    [Fact]
    public async Task WhenAnOfflinePlayerStandsInTheWay_ThenTheStepIsAccepted()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var offline = _context.AddConnectedPlayer("kitchen", x: 2, y: 0, isNamed: true);
        _context.Clients.Set("kitchen", isNamed: true, isConnected: false);
        _context.Reconcile();
        Assert.False(offline.IsConnected);

        // Act
        var result = await Step(player, GameDirection.East);

        // Assert
        Assert.True(result!.Value.IsAccepted);
    }

    [Fact]
    public async Task WhenTheClientHasNoPlayerInTheGame_ThenThereIsNoStep()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");

        // Act
        var result = await _context.StepAsync("tab-2", GameDirection.East);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenAWallStandsInFront_ThenItIsATargetObject()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var wall = _context.AddWall(-1, 2);

        // Act
        var targets = GetTargets(player);

        // Assert
        Assert.Same(wall, Assert.Single(targets));
    }

    [Fact]
    public void WhenSeveralObjectsStandInFront_ThenTheTargetsAreOrderedFromWestToEast()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var east = _context.AddWall(1, 2, width: 1, depth: 1);
        var west = _context.AddWall(0, 2, width: 1, depth: 1);
        _context.AddWall(5, 2, width: 1, depth: 1);

        // Act
        var targets = GetTargets(player);

        // Assert
        Assert.Equal(new GameObject[] { west, east }, targets);
    }
}
