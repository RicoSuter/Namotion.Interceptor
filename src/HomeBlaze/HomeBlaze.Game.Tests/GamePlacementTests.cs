using HomeBlaze.Game.Tests.Testing;

namespace HomeBlaze.Game.Tests;

public class GamePlacementTests
{
    private readonly GameTestContext _context = new();

    // Runs the world command and publishes, so the subjects show its result.
    private async Task RunAsync(Task task)
    {
        await _context.RunAsync(task);
        _context.Publish();
    }

    [Theory]
    [InlineData(GameDirection.North, -3, 2, 8, 1)]
    [InlineData(GameDirection.East, 2, -3, 1, 8)]
    [InlineData(GameDirection.South, -3, -1, 8, 1)]
    [InlineData(GameDirection.West, -1, -3, 1, 8)]
    public async Task WhenThePlayerFacesStraight_ThenThePlacementStandsAcrossTheFacingInFront(
        GameDirection facing, int x, int y, int width, int depth)
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        await _context.StepAsync(player.ClientId, facing, turnOnly: true);

        // Act
        var placement = _context.Game.GetSpotInFront(new GameCaller(player.ClientId), across: 8, along: 1);

        // Assert
        Assert.Equal(new GameSpot(x, y, width, depth, facing), placement);
    }

    [Fact]
    public async Task WhenThePlayerFacesDiagonally_ThenThePlacementIsRefused()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        await _context.StepAsync(player.ClientId, GameDirection.NorthEast, turnOnly: true);

        // Act
        var exception = Record.Exception(() => _context.Game.GetSpotInFront(new GameCaller(player.ClientId), across: 4, along: 1));

        // Assert
        Assert.Equal("Face north, east, south or west to place objects.", Assert.IsType<InvalidOperationException>(exception).Message);
    }

    [Fact]
    public void WhenThereIsNoActor_ThenThePlacementIsRefused()
    {
        // Act
        var exception = Record.Exception(() => _context.Game.GetSpotInFront(null, across: 4, along: 1));

        // Assert
        Assert.Equal("Only a player in the game can place objects.", Assert.IsType<InvalidOperationException>(exception).Message);
    }

    [Fact]
    public void WhenTheCallersPlayerIsDisconnected_ThenThePlacementIsRefused()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1", isNamed: true);
        _context.Clients.Set("tab-1", isNamed: true, isConnected: false);
        _context.Reconcile();

        // Act
        var exception = Record.Exception(() => _context.Game.GetSpotInFront(new GameCaller("tab-1"), across: 4, along: 1));

        // Assert
        Assert.Equal("Only a player in the game can place objects.", Assert.IsType<InvalidOperationException>(exception).Message);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(GameObject.MaximumSize + 1, 1)]
    public void WhenTheSizeIsOutOfRange_ThenThePlacementIsRefused(int across, int along)
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => _context.Game.GetSpotInFront(new GameCaller(player.ClientId), across, along));
    }

    [Fact]
    public async Task WhenAnObjectIsPlaced_ThenItIsAddedUnderANewNameAndWritten()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var wall = new Wall { X = 0, Y = 2, Width = 1, Depth = 4 };
        await _context.SaveAsync();
        var writesBefore = _context.Root.WriteCount;

        // Act
        await RunAsync(_context.Game.PlaceAsync(wall, CancellationToken.None));
        await _context.SaveAsync();

        // Assert
        var (name, placed) = Assert.Single(_context.Game.Objects);
        Assert.Same(wall, placed);
        Assert.StartsWith("wall-", name);
        Assert.Equal("wall-".Length + 8, name.Length);
        Assert.Equal(writesBefore + 1, _context.Root.WriteCount);
        Assert.Same(wall, Assert.Single(await _context.RunAsync(_context.Game.GetTargetsAsync(player.ClientId))));
    }

    [Fact]
    public async Task WhenPlacingATree_ThenItsNameStartsWithTreeAndADash()
    {
        // Arrange
        var tree = new Tree { X = 10, Y = 10 };

        // Act
        await RunAsync(_context.Game.PlaceAsync(tree, CancellationToken.None));

        // Assert
        var (name, placed) = Assert.Single(_context.Game.Objects);
        Assert.Same(tree, placed);
        Assert.Matches("^tree-[0-9a-f]{8}$", name);
    }

    [Fact]
    public async Task WhenSomethingIsInTheWay_ThenPlacingIsRefused()
    {
        // Arrange
        _context.AddWall(0, 4, width: 1);

        // Act
        var exception = await Record.ExceptionAsync(() =>
            RunAsync(_context.Game.PlaceAsync(new Wall { X = 0, Y = 2, Width = 1, Depth = 4 }, CancellationToken.None)));

        // Assert
        Assert.Equal("Something is in the way.", Assert.IsType<InvalidOperationException>(exception).Message);
        Assert.Single(_context.Game.Objects);
    }

    [Fact]
    public async Task WhenTheObjectDoesNotBlockMovement_ThenItMayOverlapOthers()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");

        // Act
        await RunAsync(_context.Game.PlaceAsync(new Door { X = 0, Y = 0, IsOpen = true }, CancellationToken.None));

        // Assert
        Assert.Single(_context.Game.Objects);
    }

    [Fact]
    public async Task WhenNoPlayerIsInTheGame_ThenPlacingStillWorks()
    {
        // Arrange
        var wall = new Wall { X = 50, Y = 50 };

        // Act
        await RunAsync(_context.Game.PlaceAsync(wall, CancellationToken.None));

        // Assert
        Assert.Same(wall, Assert.Single(_context.Game.Objects).Value);
    }

    [Fact]
    public void WhenAnAreaHasNoMovementBlockingObject_ThenItIsFree()
    {
        // Arrange
        var wall = _context.AddWall(0, 10);
        _context.AddDoor(10, 10, isOpen: true);

        // Act
        var isWallAreaFree = _context.Game.IsFree(wall.Area);
        var isWallAreaFreeExceptWall = _context.Game.IsFree(wall.Area, wall);
        var isOpenDoorAreaFree = _context.Game.IsFree(new GameArea(10, 10, 4, 1));

        // Assert
        Assert.False(isWallAreaFree);
        Assert.True(isWallAreaFreeExceptWall);
        Assert.True(isOpenDoorAreaFree);
    }

    [Fact]
    public async Task WhenAnotherThreadAsksWhetherAnAreaIsFree_ThenItGetsTheStateOfTheLastTick()
    {
        // Arrange
        var wall = _context.AddWall(0, 10);
        var area = new GameArea(20, 20, 4, 1);
        await RunAsync(_context.Game.PlaceAsync(new Wall { X = 20, Y = 20 }, CancellationToken.None));

        // Act
        var isWallAreaFree = await Task.Run(() => _context.Game.IsFree(wall.Area));
        var isPlacedAreaFree = await Task.Run(() => _context.Game.IsFree(area));

        // Assert
        Assert.False(isWallAreaFree);
        Assert.False(isPlacedAreaFree);
    }

    [Fact]
    public void WhenTheAreaIsLargerThanAnObjectCanBe_ThenIsFreeIsRefused()
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => _context.Game.IsFree(new GameArea(0, 0, GameObject.MaximumSize + 1, 1)));
    }

    [Fact]
    public async Task WhenTheObjectIsAlreadyPartOfTheGame_ThenPlacingIsRefused()
    {
        // Arrange
        var wall = _context.AddWall(50, 50);

        // Act
        var exception = await Record.ExceptionAsync(() => RunAsync(_context.Game.PlaceAsync(wall, CancellationToken.None)));

        // Assert
        Assert.Equal("gameObject", Assert.IsType<ArgumentException>(exception).ParamName);
    }
}
