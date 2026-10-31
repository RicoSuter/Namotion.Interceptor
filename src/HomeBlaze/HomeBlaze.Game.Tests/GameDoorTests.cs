using HomeBlaze.Game.Tests.Testing;
using HomeBlaze.Services;
using Namotion.Interceptor.Registry;

namespace HomeBlaze.Game.Tests;

public class GameDoorTests
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
    public async Task WhenADoorIsOpened_ThenItIsOpenWrittenAndThePlayerWalksThrough()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var door = _context.AddDoor(-1, 2);

        // Act
        await door.OpenAsync(CancellationToken.None);
        var results = new[] { Step(player, GameDirection.North), Step(player, GameDirection.North), Step(player, GameDirection.North) };

        // Assert
        Assert.True(door.IsOpen);
        Assert.False(door.IsMovementBlocking);
        Assert.Equal(1, _context.Root.WriteCount);
        Assert.All(results, result => Assert.True(result.IsAccepted));
        Assert.Equal((0, 3), (player.X, player.Y));
    }

    [Fact]
    public async Task WhenAnOpenDoorIsClosed_ThenItBlocksAgainAndIsWritten()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var door = _context.AddDoor(-1, 2, isOpen: true);

        // Act
        await door.CloseAsync(CancellationToken.None);
        var result = Step(player, GameDirection.North);

        // Assert
        Assert.False(door.IsOpen);
        Assert.Equal(1, _context.Root.WriteCount);
        Assert.False(result.IsAccepted);
    }

    [Fact]
    public async Task WhenAPlayerStandsInTheDoorway_ThenClosingIsRefused()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1", x: 0, y: 1);
        var door = _context.AddDoor(-1, 2, isOpen: true);

        // Act
        var exception = await Record.ExceptionAsync(() => door.CloseAsync(CancellationToken.None));

        // Assert
        Assert.Equal("Someone is in the way of the door.", Assert.IsType<InvalidOperationException>(exception).Message);
        Assert.True(door.IsOpen);
        Assert.Equal(0, _context.Root.WriteCount);
    }

    [Fact]
    public async Task WhenTheDoorIsAlreadyInTheState_ThenTheOperationIsRefused()
    {
        // Arrange
        var closed = _context.AddDoor(-1, 2);
        var open = _context.AddDoor(10, 2, isOpen: true);

        // Act
        var closing = await Record.ExceptionAsync(() => closed.CloseAsync(CancellationToken.None));
        var opening = await Record.ExceptionAsync(() => open.OpenAsync(CancellationToken.None));

        // Assert
        Assert.Equal("The door is already closed.", Assert.IsType<InvalidOperationException>(closing).Message);
        Assert.Equal("The door is already open.", Assert.IsType<InvalidOperationException>(opening).Message);
        Assert.Equal(0, _context.Root.WriteCount);
    }

    [Fact]
    public async Task WhenTheDoorOpensAndCloses_ThenOnlyTheApplicableOperationIsEnabled()
    {
        // Arrange
        var door = _context.AddDoor(-1, 2);
        var registered = door.TryGetRegisteredSubject()!;
        var open = registered.TryGetProperty("Open")!;
        var close = registered.TryGetProperty("Close")!;
        var closedState = (open.IsMethodEnabled(), close.IsMethodEnabled());

        // Act
        await door.OpenAsync(CancellationToken.None);

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
    public async Task WhenTheDoorIsNotPartOfAGame_ThenOpeningIsRefused()
    {
        // Arrange
        var door = new Door();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => door.OpenAsync(CancellationToken.None));
    }
}
