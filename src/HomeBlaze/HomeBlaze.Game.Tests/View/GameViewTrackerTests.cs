using HomeBlaze.Game.Simulation;
using HomeBlaze.Game.Tests.Testing;
using HomeBlaze.Game.View;
using HomeBlaze.Game.World;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeBlaze.Game.Tests.View;

public class GameViewTrackerTests
{
    private readonly GameViewTracker _tracker = new();
    private readonly GameWorldIndex _index = new();

    // Takes the chunks the index changed since the last update, as the view session does.
    private GameViewUpdate Update(PlayerEntity? self, int centerX = 0, int centerY = 0)
    {
        var update = _tracker.UpdateChunks(_index, _index.Changes, GameViewWindow.Around(centerX, centerY), self, isFullRefresh: false,
            NullLogger.Instance);
        _index.Changes.Clear();
        return update;
    }

    private PlayerEntity AddPlayer(string clientId, int x = 0, int y = 0)
    {
        var player = EntityFactory.Player(clientId, x, y);
        _index.AddOrUpdate(player);
        return player;
    }

    private GameEntity AddObject(GameObject gameObject)
    {
        var entity = EntityFactory.Object(gameObject);
        _index.AddOrUpdate(entity);
        return entity;
    }

    [Fact]
    public void WhenTheFirstUpdateRuns_ThenEveryVisibleEntityInTheWindowIsAdded()
    {
        // Arrange
        var player = AddPlayer("tab-1");
        AddObject(new Wall { X = -20, Y = 8, Width = 19 });
        AddObject(new Wall { X = GameViewTracker.ViewRadius + 50, Y = 0 });
        var hidden = AddPlayer("tab-2", x: 10);
        hidden.SetConnected(false);
        _index.AddOrUpdate(hidden);

        // Act
        var update = Update(player);

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
    public void WhenAPlayerDisconnects_ThenItIsRemoved()
    {
        // Arrange
        var player = AddPlayer("tab-1");
        var other = AddPlayer("tab-2", x: 10);
        var otherId = Assert.Single(Update(player).Upserted, view => !view.IsSelf).Id;

        // Act
        other.SetConnected(false);
        _index.AddOrUpdate(other);
        var update = Update(player);

        // Assert
        Assert.Equal(new[] { otherId }, update.Removed);
        Assert.Empty(update.Upserted);
        Assert.Equal(1, _tracker.Count);
    }

    [Fact]
    public void WhenAnEntityMovesBetweenChunksOfTheWindow_ThenItIsUpsertedAndKept()
    {
        // Arrange
        var player = AddPlayer("tab-1");
        var other = AddPlayer("tab-2", x: 15);
        var otherId = Assert.Single(Update(player).Upserted, view => !view.IsSelf).Id;

        // Act
        other.Area = other.Area with { X = 17 };
        _index.AddOrUpdate(other);
        var update = Update(player);

        // Assert
        var moved = Assert.Single(update.Upserted);
        Assert.Equal((otherId, 17), (moved.Id, moved.X));
        Assert.Empty(update.Removed);
        Assert.Equal(2, _tracker.Count);
    }

    [Fact]
    public void WhenAnEntityMovesOutOfTheWindow_ThenItIsRemoved()
    {
        // Arrange
        var player = AddPlayer("tab-1");
        var other = AddPlayer("tab-2", x: 10);
        var otherId = Assert.Single(Update(player).Upserted, view => !view.IsSelf).Id;

        // Act
        other.Area = other.Area with { X = 1000 };
        _index.AddOrUpdate(other);
        var update = Update(player);

        // Assert
        Assert.Equal(new[] { otherId }, update.Removed);
        Assert.Empty(update.Upserted);
    }

    [Fact]
    public void WhenTheWindowJumps_ThenTheEntitiesLeftBehindAreRemovedAndTheOnesAtTheTargetAdded()
    {
        // Arrange
        var player = AddPlayer("tab-1");
        AddObject(new Wall { X = -20, Y = 8 });
        AddObject(new Wall { X = 5000, Y = 0 });
        var nearId = Assert.Single(Update(player).Upserted, view => !view.IsSelf).Id;

        // Act
        player.Area = player.Area with { X = 5000, Y = 5 };
        _index.AddOrUpdate(player);
        var update = Update(player, 5000, 5);

        // Assert
        Assert.Equal(new[] { nearId }, update.Removed);
        Assert.Equal(2, update.Upserted.Count);
        Assert.Contains(update.Upserted, view => view is { IsSelf: true, X: 5000 });
        Assert.Contains(update.Upserted, view => view is { IsSelf: false, X: 5000, Y: 0 });
        Assert.Equal(2, _tracker.Count);
    }

    [Fact]
    public void WhenAWallSpansSeveralChunks_ThenItIsUpsertedOnceAndRemovedOnlyWhenItLeavesTheLastOne()
    {
        // Arrange
        var player = AddPlayer("tab-1");
        var wall = new Wall { X = -20, Y = 8, Width = 40 };
        var entity = AddObject(wall);
        var first = Update(player);

        // Act
        wall.Material = WallMaterial.Wood;
        entity.ReadFromSubject();
        _index.AddOrUpdate(entity);
        var changed = Update(player);
        wall.Width = 10;
        entity.ReadFromSubject();
        _index.AddOrUpdate(entity);
        var shrunk = Update(player);
        _index.Remove(entity);
        var removed = Update(player);

        // Assert
        Assert.Single(first.Upserted, view => !view.IsSelf);
        Assert.Single(changed.Upserted);
        Assert.Empty(changed.Removed);
        Assert.Equal(10, Assert.Single(shrunk.Upserted).Width);
        Assert.Empty(shrunk.Removed);
        Assert.Single(removed.Removed);
        Assert.Equal(1, _tracker.Count);
    }

    [Fact]
    public void WhenNothingChanged_ThenTheUpdateIsEmpty()
    {
        // Arrange
        var player = AddPlayer("tab-1");
        Update(player);

        // Act
        var update = Update(player);

        // Assert
        Assert.True(update.IsEmpty);
    }

    [Fact]
    public void WhenNothingChanged_ThenTheUpdateAllocatesNothing()
    {
        // Arrange
        var player = AddPlayer("tab-1");
        AddPlayer("tab-2", x: 10);
        AddObject(new Wall { X = -20, Y = 8 });
        Update(player);
        Update(player);

        // Act
        var before = GC.GetAllocatedBytesForCurrentThread();
        var update = Update(player);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        Assert.True(update.IsEmpty);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void WhenAnotherPlayerMoves_ThenOnlyItIsUpserted()
    {
        // Arrange
        var player = AddPlayer("tab-1");
        var other = AddPlayer("tab-2", x: 10);
        Update(player);

        // Act
        other.Area = other.Area with { X = 11 };
        _index.AddOrUpdate(other);
        var update = Update(player);

        // Assert
        var moved = Assert.Single(update.Upserted);
        Assert.Equal(11, moved.X);
        Assert.False(moved.IsSelf);
    }

    [Fact]
    public void WhenAPlayerLeavesTheView_ThenItIsRemoved()
    {
        // Arrange
        var player = AddPlayer("tab-1");
        var other = AddPlayer("tab-2", x: 10);
        var first = Update(player);
        var otherId = Assert.Single(first.Upserted, view => !view.IsSelf).Id;

        // Act
        _index.Remove(other);
        var update = Update(player);

        // Assert
        Assert.Equal(new[] { otherId }, update.Removed);
        Assert.Equal(1, _tracker.Count);
    }

    [Fact]
    public void WhenADoorOpens_ThenItIsUpsertedNotBlockingWithItsLeafTurned()
    {
        // Arrange
        var player = AddPlayer("tab-1");
        var door = new Door { X = -1, Y = 2 };
        var entity = AddObject(door);
        var first = Update(player);
        var closed = Assert.Single(first.Upserted, view => !view.IsSelf);

        // Act
        door.IsOpen = true;
        entity.ReadFromSubject();
        _index.AddOrUpdate(entity);
        var update = Update(player);

        // Assert
        Assert.Equal(("Door", true, 0d), (closed.Title, closed.IsMovementBlocking, closed.Parts[1].Rotation));
        var open = Assert.Single(update.Upserted);
        Assert.Equal((closed.Id, GameViewObject.ObjectKind, false, -90d), (open.Id, open.Kind, open.IsMovementBlocking, open.Parts[1].Rotation));
    }

    [Fact]
    public void WhenAPlayerTurnsItsMicrophoneOn_ThenItsViewIsUpdated()
    {
        // Arrange
        var player = AddPlayer("tab-1");
        Update(player);

        // Act
        player.IsMicrophoneOn = true;
        _index.MarkChanged(player);
        var update = Update(player);

        // Assert
        var view = Assert.Single(update.Upserted);
        Assert.True(view.IsMicrophoneOn);
        Assert.False(view.IsAudioOn);
    }

    [Fact]
    public void WhenAPlayerTurns_ThenItsViewIsUpdatedWithTheFacing()
    {
        // Arrange
        var player = AddPlayer("tab-1");
        Update(player);

        // Act
        player.Facing = GameDirection.West;
        _index.MarkChanged(player);
        var update = Update(player);

        // Assert
        Assert.Equal((int)GameDirection.West, Assert.Single(update.Upserted).Facing);
    }

    [Fact]
    public void WhenAnObjectGetsAnEqualVisual_ThenItIsNotSentAgain()
    {
        // Arrange
        var player = AddPlayer("tab-1");
        var gameObject = new VisualObject { X = 5, Y = 5, CustomVisual = GameVisual.Box(1, 2, 1) };
        var entity = AddObject(gameObject);
        Update(player);

        // Act
        gameObject.CustomVisual = GameVisual.Box(1, 2, 1);
        entity.ReadFromSubject();
        _index.AddOrUpdate(entity);
        var update = Update(player);

        // Assert
        Assert.True(update.IsEmpty);
    }

    [Fact]
    public void WhenAnObjectGetsAnotherVisual_ThenItIsUpsertedWithTheNewParts()
    {
        // Arrange
        var player = AddPlayer("tab-1");
        var gameObject = new VisualObject { X = 5, Y = 5 };
        var entity = AddObject(gameObject);
        Update(player);

        // Act
        gameObject.CustomVisual = GameVisual.Box(1, 3, 1, "#112233");
        entity.ReadFromSubject();
        _index.AddOrUpdate(entity);
        var update = Update(player);

        // Assert
        var view = Assert.Single(update.Upserted);
        var part = Assert.Single(view.Parts);
        Assert.Equal(("box", 3d, "#112233"), (part.Shape, part.Height, part.Color));
    }

    [Fact]
    public void WhenAnObjectHasAnInvalidVisual_ThenItIsSentAsTheDefaultBox()
    {
        // Arrange
        var player = AddPlayer("tab-1");
        AddObject(new VisualObject { X = 5, Y = 5, CustomVisual = GameVisual.Box(2000, 1, 1) });

        // Act
        var update = Update(player);

        // Assert
        var view = Assert.Single(update.Upserted, view => !view.IsSelf);
        var part = Assert.Single(view.Parts);
        Assert.Equal((1d, 1d, 1d, GameVisual.DefaultColor), (part.Width, part.Height, part.Depth, part.Color));
    }

    [Fact]
    public void WhenAPlayerIsInView_ThenItCarriesNoParts()
    {
        // Arrange
        var player = AddPlayer("tab-1");

        // Act
        var update = Update(player);

        // Assert
        var self = Assert.Single(update.Upserted);
        Assert.Empty(self.Parts);
        Assert.Null(self.Visual);
    }

    [Fact]
    public void WhenAPlayerIsInView_ThenItCarriesTheColorOfItsClient()
    {
        // Arrange
        var player = AddPlayer("tab-1");

        // Act
        var view = Assert.Single(Update(player).Upserted);

        // Assert
        Assert.Equal(GameColors.FromId("tab-1"), view.Color);
    }
}
