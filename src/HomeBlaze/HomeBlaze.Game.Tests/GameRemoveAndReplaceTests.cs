using HomeBlaze.Game.Tests.Testing;

namespace HomeBlaze.Game.Tests;

public class GameRemoveAndReplaceTests
{
    private readonly GameTestContext _context = new();

    // Runs the world command and publishes, so the subjects show its result.
    private async Task RunAsync(Task task)
    {
        await _context.RunAsync(task);
        _context.Publish();
    }

    [Fact]
    public async Task WhenAnObjectOfTheGameIsRemoved_ThenItIsGoneAndWritten()
    {
        // Arrange
        var wall = _context.AddWall(5, 5);
        await _context.SaveAsync();
        var writesBefore = _context.Root.WriteCount;

        // Act
        await RunAsync(_context.Game.RemoveAsync(wall, CancellationToken.None));
        await _context.SaveAsync();

        // Assert
        Assert.Empty(_context.Game.GetObjects());
        Assert.Equal(writesBefore + 1, _context.Root.WriteCount);
    }

    [Fact]
    public async Task WhenAForeignObjectIsRemoved_ThenRemovingIsRefused()
    {
        // Arrange
        _context.AddWall(5, 5);

        // Act
        var exception = await Record.ExceptionAsync(() => RunAsync(_context.Game.RemoveAsync(new Wall(), CancellationToken.None)));

        // Assert
        Assert.Equal("The object is not part of this game.", Assert.IsType<InvalidOperationException>(exception).Message);
        Assert.Single(_context.Game.GetObjects());
    }

    [Fact]
    public async Task WhenAnObjectIsReplaced_ThenTheReplacementsTakeItsPlaceUnderNewNames()
    {
        // Arrange
        var wall = _context.AddWall(0, 10, width: 10);
        var west = new Wall { X = 0, Y = 10, Width = 3 };
        var door = new Door { X = 3, Y = 10, Width = 4 };
        var east = new Wall { X = 7, Y = 10, Width = 3 };
        await _context.SaveAsync();
        var writesBefore = _context.Root.WriteCount;

        // Act
        await RunAsync(_context.Game.ReplaceAsync(wall, [west, door, east], CancellationToken.None));
        await _context.SaveAsync();

        // Assert
        var objects = _context.Game.GetObjects();
        Assert.Equal(3, objects.Count);
        Assert.DoesNotContain(wall, objects.Values);
        Assert.Equal(2, objects.Count(pair => pair.Key.StartsWith("wall-", StringComparison.Ordinal)));
        Assert.Same(door, Assert.Single(objects, pair => pair.Key.StartsWith("door-", StringComparison.Ordinal)).Value);
        Assert.Equal(writesBefore + 1, _context.Root.WriteCount);
    }

    [Fact]
    public async Task WhenAReplacementWouldOverlapAnotherObject_ThenNothingChanges()
    {
        // Arrange
        var wall = _context.AddWall(0, 10, width: 10);
        var other = _context.AddWall(12, 10);
        var before = new Dictionary<string, GameObject>(_context.Game.GetObjects());

        // Act
        var exception = await Record.ExceptionAsync(() =>
            RunAsync(_context.Game.ReplaceAsync(wall, [new Wall { X = 5, Y = 10, Width = 10 }], CancellationToken.None)));

        // Assert
        Assert.Equal("Something is in the way.", Assert.IsType<InvalidOperationException>(exception).Message);
        Assert.Equal(before, _context.Game.GetObjects());
        Assert.Contains(other, _context.Game.GetObjects().Values);
    }

    [Fact]
    public async Task WhenTwoReplacementsOverlapEachOther_ThenNothingChanges()
    {
        // Arrange
        var wall = _context.AddWall(0, 10, width: 10);
        var before = new Dictionary<string, GameObject>(_context.Game.GetObjects());

        // Act
        var exception = await Record.ExceptionAsync(() => RunAsync(_context.Game.ReplaceAsync(wall,
            [new Wall { X = 0, Y = 10, Width = 6 }, new Wall { X = 4, Y = 10, Width = 6 }], CancellationToken.None)));

        // Assert
        Assert.Equal("Something is in the way.", Assert.IsType<InvalidOperationException>(exception).Message);
        Assert.Equal(before, _context.Game.GetObjects());
    }

    [Fact]
    public async Task WhenAReplacementIsListedTwice_ThenReplacingIsRefused()
    {
        // Arrange
        var wall = _context.AddWall(0, 10, width: 10);
        var door = new Door { X = 3, Y = 10, IsOpen = true };

        // Act
        var exception = await Record.ExceptionAsync(() =>
            RunAsync(_context.Game.ReplaceAsync(wall, [door, door], CancellationToken.None)));

        // Assert
        Assert.IsType<ArgumentException>(exception);
        Assert.Same(wall, Assert.Single(_context.Game.GetObjects()).Value);
    }

    [Fact]
    public async Task WhenAnObjectIsRemoved_ThenPlayersWalkWhereItStood()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1", x: 0, y: 8);
        var wall = _context.AddWall(-1, 10);

        // Act
        await RunAsync(_context.Game.RemoveAsync(wall, CancellationToken.None));
        var step = await _context.StepAsync(player.ClientId, GameDirection.North);

        // Assert
        Assert.True(step!.Value.IsAccepted);
    }

    [Fact]
    public async Task WhenAnObjectIsReplaced_ThenThePlayersMoveAgainstTheReplacements()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1", x: 3, y: 8);
        var wall = _context.AddWall(0, 10, width: 10);
        var door = new Door { X = 3, Y = 10, Width = 4, IsOpen = true };

        // Act
        await RunAsync(_context.Game.ReplaceAsync(wall,
            [new Wall { X = 0, Y = 10, Width = 3 }, door, new Wall { X = 7, Y = 10, Width = 3 }],
            CancellationToken.None));
        var throughDoor = await _context.StepAsync(player.ClientId, GameDirection.North);
        var intoWall = await _context.StepAsync(player.ClientId, GameDirection.West);

        // Assert
        Assert.True(throughDoor!.Value.IsAccepted);
        Assert.False(intoWall!.Value.IsAccepted);
    }

    [Fact]
    public async Task WhenAReplacementHasAnInvalidSize_ThenReplacingIsRefused()
    {
        // Arrange
        var wall = _context.AddWall(0, 10);

        // Act
        var exception = await Record.ExceptionAsync(() =>
            RunAsync(_context.Game.ReplaceAsync(wall, [new Wall { X = 0, Y = 10, Height = GameObject.MaximumSize + 1 }], CancellationToken.None)));

        // Assert
        Assert.Equal("replacements", Assert.IsType<ArgumentException>(exception).ParamName);
        Assert.Same(wall, Assert.Single(_context.Game.GetObjects()).Value);
    }

    [Fact]
    public async Task WhenAForeignObjectIsReplaced_ThenReplacingIsRefused()
    {
        // Arrange
        _context.AddWall(0, 10);

        // Act
        var exception = await Record.ExceptionAsync(() =>
            RunAsync(_context.Game.ReplaceAsync(new Wall(), [new Wall { X = 20, Y = 20 }], CancellationToken.None)));

        // Assert
        Assert.Equal("The object is not part of this game.", Assert.IsType<InvalidOperationException>(exception).Message);
        Assert.Single(_context.Game.GetObjects());
    }
}
