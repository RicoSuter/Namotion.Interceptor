using HomeBlaze.Game.Tests.Testing;
using HomeBlaze.Services;
using Namotion.Interceptor.Registry;

namespace HomeBlaze.Game.Buildings.Tests;

public class DoorTests
{
    private readonly GameTestContext _context = new();

    private MoveResult Step(GamePlayer player, GameDirection direction)
    {
        _context.TimeProvider.Advance(Game.StepInterval);
        return _context.Game.TryMove(player, direction, turnOnly: false);
    }

    [Fact]
    public void WhenADoorIsCreated_ThenItIsAClosedBlockingWallSegment()
    {
        // Act
        var door = new Door();

        // Assert
        Assert.Equal((4, 1, 10), (door.Width, door.Depth, door.Height));
        Assert.False(door.IsOpen);
        Assert.True(door.IsMovementBlocking);
        Assert.True(door.IsVisible);
    }

    [Fact]
    public void WhenADoorIsClosed_ThenThePlayerCannotStepIntoIt()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        _context.AddDoor(-1, 2);

        // Act
        var result = Step(player, GameDirection.North);

        // Assert
        Assert.False(result.IsAccepted);
        Assert.Equal((0, 0), (player.X, player.Y));
    }

    [Fact]
    public void WhenADoorIsOpened_ThenItIsOpenAndThePlayerWalksThrough()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var door = _context.AddDoor(-1, 2);

        // Act
        door.Open();
        // Stands in for the change observer of the running game, which rebuilds the index after a direct write.
        _context.Game.RequestIndexRebuild();
        var results = new[] { Step(player, GameDirection.North), Step(player, GameDirection.North), Step(player, GameDirection.North) };

        // Assert
        Assert.True(door.IsOpen);
        Assert.False(door.IsMovementBlocking);
        Assert.All(results, result => Assert.True(result.IsAccepted));
        Assert.Equal((0, 3), (player.X, player.Y));
    }

    [Fact]
    public void WhenAnOpenDoorIsClosed_ThenItBlocksAgain()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var door = _context.AddDoor(-1, 2, isOpen: true);

        // Act
        door.Close();
        // Stands in for the change observer of the running game, which rebuilds the index after a direct write.
        _context.Game.RequestIndexRebuild();
        var result = Step(player, GameDirection.North);

        // Assert
        Assert.False(door.IsOpen);
        Assert.False(result.IsAccepted);
    }

    [Fact]
    public void WhenAPlayerStandsInTheDoorway_ThenCloseIsRefusedAndTheDoorStaysOpen()
    {
        // Arrange
        var door = _context.AddDoor(0, 4, isOpen: true);
        _context.AddConnectedPlayer("tab-1", x: 1, y: 3);

        // Act
        var exception = Assert.Throws<InvalidOperationException>(door.Close);

        // Assert
        Assert.Equal("Something is in the way.", exception.Message);
        Assert.True(door.IsOpen);
    }

    [Fact]
    public void WhenAnotherOpenDoorOverlapsTheDoor_ThenClosingWorks()
    {
        // Arrange
        var door = _context.AddDoor(0, 4, isOpen: true);
        _context.AddDoor(2, 4, isOpen: true);

        // Act
        door.Close();

        // Assert
        Assert.False(door.IsOpen);
    }

    [Fact]
    public void WhenTheDoorIsAlreadyInTheState_ThenTheOperationIsRefused()
    {
        // Arrange
        var closed = _context.AddDoor(-1, 2);
        var open = _context.AddDoor(10, 2, isOpen: true);

        // Act
        var closing = Record.Exception(closed.Close);
        var opening = Record.Exception(open.Open);

        // Assert
        Assert.Equal("The door is already closed.", Assert.IsType<InvalidOperationException>(closing).Message);
        Assert.Equal("The door is already open.", Assert.IsType<InvalidOperationException>(opening).Message);
    }

    [Fact]
    public void WhenTheDoorOpensAndCloses_ThenOnlyTheApplicableOperationIsEnabled()
    {
        // Arrange
        var door = _context.AddDoor(-1, 2);
        var registered = door.TryGetRegisteredSubject()!;
        var open = registered.TryGetProperty("Open")!;
        var close = registered.TryGetProperty("Close")!;
        var closedState = (open.IsMethodEnabled(), close.IsMethodEnabled());

        // Act
        door.Open();

        // Assert
        Assert.Equal((true, false), closedState);
        Assert.Equal((false, true), (open.IsMethodEnabled(), close.IsMethodEnabled()));
    }

    [Fact]
    public void WhenADoorOpens_ThenItsLeafTurnsAQuarterAroundTheHinge()
    {
        // Arrange
        var door = new Door();
        var closed = door.Visual;

        // Act
        door.IsOpen = true;
        var open = door.Visual;

        // Assert
        Assert.Equal(2, closed.Parts.Length);
        var lintel = closed.Parts[0];
        Assert.Equal(new GameVisualVector(0, Door.LeafHeight, 0), lintel.Offset);
        Assert.Equal(new GameVisualVector(4, 10 - Door.LeafHeight, 1), lintel.Size);
        var leaf = closed.Parts[1];
        Assert.Equal(new GameVisualPoint(0, 0.5), leaf.Pivot);
        Assert.Equal(new GameVisualVector(4, Door.LeafHeight, 0.3), leaf.Size);
        Assert.Equal(0, leaf.Rotation);
        Assert.Equal(-90, open.Parts[1].Rotation);
    }

    [Fact]
    public void WhenADoorRunsNorthToSouth_ThenItsLeafHangsAtTheSouthEnd()
    {
        // Arrange
        var door = new Door { Width = 1, Depth = 4 };

        // Act
        var leaf = door.Visual.Parts[1];

        // Assert
        Assert.Equal(new GameVisualPoint(0.5, 0), leaf.Pivot);
        Assert.Equal(new GameVisualVector(0.3, Door.LeafHeight, 4), leaf.Size);
        Assert.Equal(new GameVisualVector(0.35, 0, 0), leaf.Offset);
    }

    [Fact]
    public void WhenADoorIsNoTallerThanItsLeaf_ThenItHasNoLintel()
    {
        // Arrange
        var door = new Door { Height = 8 };

        // Act
        var part = Assert.Single(door.Visual.Parts);

        // Assert
        Assert.Equal(new GameVisualVector(4, 8, 0.3), part.Size);
    }

    [Fact]
    public void WhenTheDoorIsNotPartOfAGame_ThenClosingIsRefused()
    {
        // Arrange
        var door = new Door { IsOpen = true };

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(door.Close);
        Assert.Equal("The object is not part of a game.", exception.Message);
        Assert.True(door.IsOpen);
    }
}
