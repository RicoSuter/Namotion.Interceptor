using HomeBlaze.Game.Tests.Testing;
using HomeBlaze.Services;
using Namotion.Interceptor.Registry;

namespace HomeBlaze.Game.Tests.Objects.Buildings;

public class WallDoorTests
{
    private readonly GameTestContext _context = new();

    private (Wall Before, Door Door, Wall After) GetPieces()
    {
        var walls = _context.Game.Objects.Values.OfType<Wall>().OrderBy(wall => wall.X).ThenBy(wall => wall.Y).ToArray();
        Assert.Equal(2, walls.Length);
        return (walls[0], Assert.Single(_context.Game.Objects.Values.OfType<Door>()), walls[1]);
    }

    [Fact]
    public async Task WhenADoorIsAddedToAnEastWestWall_ThenTheWallIsSplitAroundThePlayer()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var wall = _context.AddWall(-4, 2, width: 16);

        // Act
        await wall.AddDoorAsync(null, new GameCaller(player), CancellationToken.None);

        // Assert
        var (before, door, after) = GetPieces();
        Assert.Equal((-4, 2, 3, 1), (before.X, before.Y, before.Width, before.Depth));
        Assert.Equal((-1, 2, 4, 1, false), (door.X, door.Y, door.Width, door.Depth, door.IsOpen));
        Assert.Equal((3, 2, 9, 1), (after.X, after.Y, after.Width, after.Depth));
        Assert.Equal(3, _context.Game.Objects.Count);
        Assert.Equal(1, _context.Root.WriteCount);
    }

    [Fact]
    public async Task WhenADoorIsAddedToANorthSouthWall_ThenTheWallIsSplitAroundThePlayer()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        _context.Game.TryMove(player, GameDirection.East, turnOnly: true);
        var wall = _context.AddWall(2, -4, width: 1, depth: 16);

        // Act
        await wall.AddDoorAsync(6, new GameCaller(player), CancellationToken.None);

        // Assert
        var walls = _context.Game.Objects.Values.OfType<Wall>().OrderBy(piece => piece.Y).ToArray();
        var door = Assert.Single(_context.Game.Objects.Values.OfType<Door>());
        Assert.Equal((2, -4, 1, 2), (walls[0].X, walls[0].Y, walls[0].Width, walls[0].Depth));
        Assert.Equal((2, -2, 1, 6), (door.X, door.Y, door.Width, door.Depth));
        Assert.Equal((2, 4, 1, 8), (walls[1].X, walls[1].Y, walls[1].Width, walls[1].Depth));
    }

    [Fact]
    public async Task WhenThePlayerStandsNearTheEnd_ThenTheDoorMovesInwardKeepingOneWallTile()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1", x: -1);
        var wall = _context.AddWall(0, 2, width: 8);

        // Act
        await wall.AddDoorAsync(4, new GameCaller(player), CancellationToken.None);

        // Assert
        var (before, door, after) = GetPieces();
        Assert.Equal((0, 1), (before.X, before.Width));
        Assert.Equal((1, 4), (door.X, door.Width));
        Assert.Equal((5, 3), (after.X, after.Width));
    }

    [Fact]
    public async Task WhenADoorIsAdded_ThenThePiecesKeepTheMaterialAndHeight()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var wall = _context.AddWall(-4, 2, width: 16);
        wall.Material = WallMaterial.Concrete;
        wall.Height = 12;

        // Act
        await wall.AddDoorAsync(null, new GameCaller(player), CancellationToken.None);

        // Assert
        var (before, door, after) = GetPieces();
        Assert.All(new[] { before, after }, piece => Assert.Equal((WallMaterial.Concrete, 12), (piece.Material, piece.Height)));
        Assert.Equal((WallMaterial.Concrete, 12), (door.Material, door.Height));
    }

    [Fact]
    public async Task WhenThereIsNoActor_ThenTheDoorIsCenteredOnTheWall()
    {
        // Arrange
        var wall = _context.AddWall(0, 20, width: 10);

        // Act
        await wall.AddDoorAsync(4, null, CancellationToken.None);

        // Assert
        var (before, door, after) = GetPieces();
        Assert.Equal((3, 3, 3), (before.Width, door.X, after.Width));
    }

    [Fact]
    public async Task WhenTheDoorDoesNotFit_ThenAddingIsRefused()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var wall = _context.AddWall(-1, 2, width: 5);

        // Act
        var exception = await Record.ExceptionAsync(() => wall.AddDoorAsync(4, new GameCaller(player), CancellationToken.None));

        // Assert
        Assert.Equal("This wall is too short for a door of 4 tiles; it fits up to 3.", Assert.IsType<InvalidOperationException>(exception).Message);
        Assert.Same(wall, Assert.Single(_context.Game.Objects).Value);
        Assert.Equal(0, _context.Root.WriteCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    public async Task WhenTheWidthIsOutOfRange_ThenAddingIsRefused(int width)
    {
        // Arrange
        var wall = _context.AddWall(0, 20, width: 20);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => wall.AddDoorAsync(width, null, CancellationToken.None));
    }

    [Fact]
    public async Task WhenTheWallIsThickerThanOneTile_ThenAddingIsRefused()
    {
        // Arrange
        var wall = _context.AddWall(0, 20, width: 6, depth: 2);

        // Act
        var exception = await Record.ExceptionAsync(() => wall.AddDoorAsync(null, null, CancellationToken.None));

        // Assert
        Assert.Equal("Only a wall one tile thick can take a door.", Assert.IsType<InvalidOperationException>(exception).Message);
    }

    [Fact]
    public void WhenTheWallIsShorterThanFourTiles_ThenAddDoorIsDisabled()
    {
        // Arrange
        var wall = _context.AddWall(0, 20, width: 4);
        var addDoor = wall.TryGetRegisteredSubject()!.TryGetProperty("AddDoor")!;
        var isEnabledAtFour = addDoor.IsMethodEnabled();

        // Act
        wall.Width = 3;

        // Assert
        Assert.True(isEnabledAtFour);
        Assert.False(addDoor.IsMethodEnabled());
    }

    [Fact]
    public async Task WhenAPieceWouldOverlapAnotherObject_ThenNothingChanges()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var wall = _context.AddWall(-4, 2, width: 16);
        // A hand-edited overlap: another wall on the last tile of the first one.
        _context.AddWall(11, 2, width: 1);

        // Act
        var exception = await Record.ExceptionAsync(() => wall.AddDoorAsync(null, new GameCaller(player), CancellationToken.None));

        // Assert
        Assert.Equal("Something is in the way.", Assert.IsType<InvalidOperationException>(exception).Message);
        Assert.Equal(2, _context.Game.Objects.Count);
        Assert.Contains(wall, _context.Game.Objects.Values);
    }
}
