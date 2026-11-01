using HomeBlaze.Game.Tests.Testing;

namespace HomeBlaze.Game.Tests;

public class GameObjectsTests
{
    private readonly GameTestContext _context = new();

    [Fact]
    public async Task WhenAnObjectOfTheGameIsRemoved_ThenItIsGoneAndWritten()
    {
        // Arrange
        var wall = _context.AddWall(5, 5);

        // Act
        await _context.Game.RemoveAsync(wall, CancellationToken.None);

        // Assert
        Assert.Empty(_context.Game.Objects);
        Assert.Equal(1, _context.Root.WriteCount);
    }

    [Fact]
    public async Task WhenAForeignObjectIsRemoved_ThenRemovingIsRefused()
    {
        // Arrange
        _context.AddWall(5, 5);

        // Act
        var exception = await Record.ExceptionAsync(() => _context.Game.RemoveAsync(new Wall(), CancellationToken.None));

        // Assert
        Assert.Equal("The object is not part of this game.", Assert.IsType<InvalidOperationException>(exception).Message);
        Assert.Single(_context.Game.Objects);
        Assert.Equal(0, _context.Root.WriteCount);
    }

    [Fact]
    public async Task WhenAnObjectIsReplaced_ThenTheReplacementsTakeItsPlaceUnderNewNames()
    {
        // Arrange
        var wall = _context.AddWall(0, 10, width: 10);
        var west = new Wall { X = 0, Y = 10, Width = 3 };
        var door = new Door { X = 3, Y = 10, Width = 4 };
        var east = new Wall { X = 7, Y = 10, Width = 3 };

        // Act
        await _context.Game.ReplaceAsync(wall, [west, door, east], CancellationToken.None);

        // Assert
        var objects = _context.Game.Objects;
        Assert.Equal(3, objects.Count);
        Assert.DoesNotContain(wall, objects.Values);
        Assert.Equal(2, objects.Count(pair => pair.Key.StartsWith("wall-", StringComparison.Ordinal)));
        Assert.Same(door, Assert.Single(objects, pair => pair.Key.StartsWith("door-", StringComparison.Ordinal)).Value);
        Assert.Equal(1, _context.Root.WriteCount);
    }

    [Fact]
    public async Task WhenAReplacementWouldOverlapAnotherObject_ThenNothingChanges()
    {
        // Arrange
        var wall = _context.AddWall(0, 10, width: 10);
        var other = _context.AddWall(12, 10);
        var before = new Dictionary<string, GameObject>(_context.Game.Objects);

        // Act
        var exception = await Record.ExceptionAsync(() =>
            _context.Game.ReplaceAsync(wall, [new Wall { X = 5, Y = 10, Width = 10 }], CancellationToken.None));

        // Assert
        Assert.Equal("Something is in the way.", Assert.IsType<InvalidOperationException>(exception).Message);
        Assert.Equal(before, _context.Game.Objects);
        Assert.Contains(other, _context.Game.Objects.Values);
        Assert.Equal(0, _context.Root.WriteCount);
    }

    [Fact]
    public async Task WhenTwoReplacementsOverlapEachOther_ThenNothingChanges()
    {
        // Arrange
        var wall = _context.AddWall(0, 10, width: 10);
        var before = new Dictionary<string, GameObject>(_context.Game.Objects);

        // Act
        var exception = await Record.ExceptionAsync(() => _context.Game.ReplaceAsync(wall,
            [new Wall { X = 0, Y = 10, Width = 6 }, new Wall { X = 4, Y = 10, Width = 6 }], CancellationToken.None));

        // Assert
        Assert.Equal("Something is in the way.", Assert.IsType<InvalidOperationException>(exception).Message);
        Assert.Equal(before, _context.Game.Objects);
        Assert.Equal(0, _context.Root.WriteCount);
    }

    [Fact]
    public async Task WhenAReplacementIsListedTwice_ThenReplacingIsRefused()
    {
        // Arrange
        var wall = _context.AddWall(0, 10, width: 10);
        var door = new Door { X = 3, Y = 10, IsOpen = true };

        // Act
        var exception = await Record.ExceptionAsync(() =>
            _context.Game.ReplaceAsync(wall, [door, door], CancellationToken.None));

        // Assert
        Assert.IsType<ArgumentException>(exception);
        Assert.Same(wall, Assert.Single(_context.Game.Objects).Value);
        Assert.Equal(0, _context.Root.WriteCount);
    }

    [Fact]
    public async Task WhenAnObjectIsRemoved_ThenPlayersWalkWhereItStood()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1", x: 0, y: 8);
        var wall = _context.AddWall(-1, 10);

        // Act
        await _context.Game.RemoveAsync(wall, CancellationToken.None);
        _context.TimeProvider.Advance(Game.StepInterval);
        var step = _context.Game.TryMove(player, GameDirection.North, turnOnly: false);

        // Assert
        Assert.True(step.IsAccepted);
    }

    [Fact]
    public async Task WhenAnObjectIsReplaced_ThenThePlayersMoveAgainstTheReplacements()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1", x: 3, y: 8);
        var wall = _context.AddWall(0, 10, width: 10);
        var door = new Door { X = 3, Y = 10, Width = 4, IsOpen = true };

        // Act
        await _context.Game.ReplaceAsync(wall,
            [new Wall { X = 0, Y = 10, Width = 3 }, door, new Wall { X = 7, Y = 10, Width = 3 }],
            CancellationToken.None);
        _context.TimeProvider.Advance(Game.StepInterval);
        var throughDoor = _context.Game.TryMove(player, GameDirection.North, turnOnly: false);
        _context.TimeProvider.Advance(Game.StepInterval);
        var intoWall = _context.Game.TryMove(player, GameDirection.West, turnOnly: false);

        // Assert
        Assert.True(throughDoor.IsAccepted);
        Assert.False(intoWall.IsAccepted);
    }

    [Fact]
    public async Task WhenAReplacementHasAnInvalidSize_ThenReplacingIsRefused()
    {
        // Arrange
        var wall = _context.AddWall(0, 10);

        // Act
        var exception = await Record.ExceptionAsync(() =>
            _context.Game.ReplaceAsync(wall, [new Wall { X = 0, Y = 10, Height = GameObject.MaximumSize + 1 }], CancellationToken.None));

        // Assert
        Assert.Equal("replacements", Assert.IsType<ArgumentException>(exception).ParamName);
        Assert.Same(wall, Assert.Single(_context.Game.Objects).Value);
        Assert.Equal(0, _context.Root.WriteCount);
    }

    [Fact]
    public async Task WhenAForeignObjectIsReplaced_ThenReplacingIsRefused()
    {
        // Arrange
        _context.AddWall(0, 10);

        // Act
        var exception = await Record.ExceptionAsync(() =>
            _context.Game.ReplaceAsync(new Wall(), [new Wall { X = 20, Y = 20 }], CancellationToken.None));

        // Assert
        Assert.Equal("The object is not part of this game.", Assert.IsType<InvalidOperationException>(exception).Message);
        Assert.Single(_context.Game.Objects);
    }
}
