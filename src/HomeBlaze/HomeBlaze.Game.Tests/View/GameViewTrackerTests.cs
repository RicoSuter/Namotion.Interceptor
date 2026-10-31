using HomeBlaze.Game.Tests.Testing;
using HomeBlaze.Game.View;

namespace HomeBlaze.Game.Tests.View;

public class GameViewTrackerTests
{
    private readonly GameTestContext _context = new();
    private readonly GameViewTracker _tracker = new();

    [Fact]
    public void WhenTheViewIsFirstUpdated_ThenEveryObjectInTheRadiusIsAdded()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        _context.AddWall(-20, 8, width: 19);
        _context.AddWall(GameViewTracker.ViewRadius + 50, 0);

        // Act
        var update = _context.Game.UpdateView(_tracker, player);

        // Assert
        Assert.Equal(2, update.Upserted.Count);
        var self = Assert.Single(update.Upserted, view => view.IsSelf);
        Assert.Equal(GameViewObject.PlayerKind, self.Kind);
        Assert.Equal((0, 0, 2, 2, 7), (self.X, self.Y, self.Width, self.Depth, self.Height));
        var wall = Assert.Single(update.Upserted, view => view.Kind == GameViewObject.ObjectKind);
        Assert.Equal((-20, 8, 19), (wall.X, wall.Y, wall.Width));
        Assert.True(wall.IsMovementBlocking);
        Assert.Equal(19d, Assert.Single(wall.Parts).Width);
        Assert.Empty(update.Removed);
    }

    [Fact]
    public void WhenNothingChanged_ThenTheUpdateIsEmpty()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        _context.Game.UpdateView(_tracker, player);

        // Act
        var update = _context.Game.UpdateView(_tracker, player);

        // Assert
        Assert.True(update.IsEmpty);
    }

    [Fact]
    public void WhenNothingChanged_ThenTheUpdateAllocatesNothing()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        _context.AddConnectedPlayer("tab-2", x: 10, y: 0);
        _context.AddWall(-20, 8);
        _context.Game.UpdateView(_tracker, player);
        _context.Game.UpdateView(_tracker, player);

        // Act
        var before = GC.GetAllocatedBytesForCurrentThread();
        var update = _context.Game.UpdateView(_tracker, player);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        Assert.True(update.IsEmpty);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void WhenAnotherPlayerMoves_ThenOnlyItIsUpserted()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var other = _context.AddConnectedPlayer("tab-2", x: 10, y: 0);
        _context.Game.UpdateView(_tracker, player);

        // Act
        _context.TimeProvider.Advance(Game.StepInterval);
        _context.Game.TryMove(other, GameDirection.East, turnOnly: false);
        var update = _context.Game.UpdateView(_tracker, player);

        // Assert
        var moved = Assert.Single(update.Upserted);
        Assert.Equal(11, moved.X);
        Assert.False(moved.IsSelf);
    }

    [Fact]
    public void WhenAPlayerDisconnects_ThenItIsRemovedFromTheView()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        _context.AddConnectedPlayer("tab-2", x: 10, y: 0);
        var first = _context.Game.UpdateView(_tracker, player);
        var otherId = Assert.Single(first.Upserted, view => !view.IsSelf).Id;

        // Act
        _context.Clients.Set("tab-2", isNamed: false, isConnected: false);
        _context.Game.ReconcilePlayers();
        var update = _context.Game.UpdateView(_tracker, player);

        // Assert
        Assert.Equal(new[] { otherId }, update.Removed);
        Assert.Single(_tracker.Known);
    }

    [Fact]
    public async Task WhenADoorOpens_ThenItIsUpsertedNotBlockingWithItsLeafTurned()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var door = _context.AddDoor(-1, 2);
        var first = _context.Game.UpdateView(_tracker, player);
        var closed = Assert.Single(first.Upserted, view => !view.IsSelf);

        // Act
        await door.OpenAsync(CancellationToken.None);
        var update = _context.Game.UpdateView(_tracker, player);

        // Assert
        Assert.Equal(("Door", true, 0d), (closed.Title, closed.IsMovementBlocking, closed.Parts[1].Rotation));
        var open = Assert.Single(update.Upserted);
        Assert.Equal((closed.Id, GameViewObject.ObjectKind, false, -90d), (open.Id, open.Kind, open.IsMovementBlocking, open.Parts[1].Rotation));
    }

    [Fact]
    public void WhenAPlayerTurnsItsMicrophoneOn_ThenItsViewIsUpdated()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        _context.Game.UpdateView(_tracker, player);

        // Act
        _context.Game.SetMicrophone(new GameCaller(player), pageId: 1, isOn: true);
        var update = _context.Game.UpdateView(_tracker, player);

        // Assert
        var view = Assert.Single(update.Upserted);
        Assert.True(view.IsMicrophoneOn);
        Assert.False(view.IsAudioOn);
    }

    [Fact]
    public void WhenAnObjectGetsAnEqualVisual_ThenItIsNotSentAgain()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var gameObject = _context.AddObject("object", new VisualObject { X = 5, Y = 5, CustomVisual = GameVisual.Box(1, 2, 1) });
        _context.Game.UpdateView(_tracker, player);

        // Act
        gameObject.CustomVisual = GameVisual.Box(1, 2, 1);
        var update = _context.Game.UpdateView(_tracker, player);

        // Assert
        Assert.True(update.IsEmpty);
    }

    [Fact]
    public void WhenAnObjectGetsAnotherVisual_ThenItIsUpsertedWithTheNewParts()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var gameObject = _context.AddObject("object", new VisualObject { X = 5, Y = 5 });
        _context.Game.UpdateView(_tracker, player);

        // Act
        gameObject.CustomVisual = GameVisual.Box(1, 3, 1, "#112233");
        var update = _context.Game.UpdateView(_tracker, player);

        // Assert
        var view = Assert.Single(update.Upserted);
        var part = Assert.Single(view.Parts);
        Assert.Equal(("box", 3d, "#112233"), (part.Shape, part.Height, part.Color));
    }

    [Fact]
    public void WhenAnObjectHasAnInvalidVisual_ThenItIsSentAsTheDefaultBox()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        _context.AddObject("object", new VisualObject { X = 5, Y = 5, CustomVisual = GameVisual.Box(2000, 1, 1) });

        // Act
        var update = _context.Game.UpdateView(_tracker, player);

        // Assert
        var view = Assert.Single(update.Upserted, view => !view.IsSelf);
        var part = Assert.Single(view.Parts);
        Assert.Equal((1d, 1d, 1d, GameVisual.DefaultColor), (part.Width, part.Height, part.Depth, part.Color));
    }

    [Fact]
    public void WhenAPlayerIsInView_ThenItCarriesNoParts()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");

        // Act
        var update = _context.Game.UpdateView(_tracker, player);

        // Assert
        var self = Assert.Single(update.Upserted);
        Assert.Empty(self.Parts);
        Assert.Null(self.Visual);
    }

    [Fact]
    public void WhenThePlayerColorIsRequested_ThenItIsTheColorOfItsViewObject()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var view = Assert.Single(_context.Game.UpdateView(_tracker, player).Upserted);

        // Act
        var color = _tracker.GetPlayerColor(player);

        // Assert
        Assert.Equal(view.Color, color);
        Assert.Equal(GameColors.FromId("tab-1"), color);
    }
}
