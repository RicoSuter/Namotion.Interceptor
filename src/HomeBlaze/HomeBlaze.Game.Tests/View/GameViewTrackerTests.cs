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
    private readonly GameViewPayloads _payloads = new(NullLogger.Instance);

    // Takes the entities the index changed since the last update, as the view session does.
    private GameViewUpdate Update(PlayerEntity? self, int centerX = 0, int centerY = 0) =>
        Update(_tracker, self, centerX, centerY);

    private GameViewUpdate Update(GameViewTracker tracker, PlayerEntity? self, int centerX = 0, int centerY = 0, bool isLastPage = true)
    {
        var update = tracker.Update(_index, _index.EntityChanges.AsSpan(), GameViewWindow.Around(centerX, centerY), self,
            isFullRefresh: false, _payloads);
        if (isLastPage)
        {
            _index.ClearChanges();
            _payloads.Clear();
        }

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
        Assert.Equal(GameViewPayload.PlayerKind, self.Payload.Kind);
        Assert.Equal((0, 0, 2, 2, 7), (self.Payload.X, self.Payload.Y, self.Payload.Width, self.Payload.Depth, self.Payload.Height));
        var wall = Assert.Single(update.Upserted, view => view.Payload.Kind == GameViewPayload.ObjectKind);
        Assert.Equal((-20, 8, 19), (wall.Payload.X, wall.Payload.Y, wall.Payload.Width));
        Assert.True(wall.Payload.IsMovementBlocking);
        Assert.Equal(19d, Assert.Single(wall.Payload.Parts).Width);
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
        Assert.Equal((otherId, 17), (moved.Id, moved.Payload.X));
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
        Assert.Contains(update.Upserted, view => view is { IsSelf: true, Payload.X: 5000 });
        Assert.Contains(update.Upserted, view => view is { IsSelf: false, Payload.X: 5000, Payload.Y: 0 });
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
        Assert.Equal(10, Assert.Single(shrunk.Upserted).Payload.Width);
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
        Assert.Equal(11, moved.Payload.X);
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
        Assert.Equal(("Door", true, 0d),
            (closed.Payload.Title, closed.Payload.IsMovementBlocking, closed.Payload.Parts[1].Rotation));
        var open = Assert.Single(update.Upserted);
        Assert.Equal((closed.Id, GameViewPayload.ObjectKind, false, -90d),
            (open.Id, open.Payload.Kind, open.Payload.IsMovementBlocking, open.Payload.Parts[1].Rotation));
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
        Assert.True(view.Payload.IsMicrophoneOn);
        Assert.False(view.Payload.IsAudioOn);
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
        Assert.Equal((int)GameDirection.West, Assert.Single(update.Upserted).Payload.Facing);
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
        var part = Assert.Single(view.Payload.Parts);
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
        var part = Assert.Single(view.Payload.Parts);
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
        Assert.Empty(self.Payload.Parts);
    }

    [Fact]
    public void WhenAPlayerIsInView_ThenItCarriesTheColorOfItsClient()
    {
        // Arrange
        var player = AddPlayer("tab-1");

        // Act
        var view = Assert.Single(Update(player).Upserted);

        // Assert
        Assert.Equal(GameColors.FromId("tab-1"), view.Payload.Color);
    }

    [Fact]
    public void WhenOnlyTheHeightOfAnObjectChanges_ThenItIsUpserted()
    {
        // Arrange
        var player = AddPlayer("tab-1");
        var entity = AddObject(new VisualObject { X = 5, Y = 5 });
        Update(player);

        // Act
        entity.Height = 4;
        _index.AddOrUpdate(entity);
        var update = Update(player);

        // Assert
        Assert.Equal(4, Assert.Single(update.Upserted).Payload.Height);
    }

    [Fact]
    public void WhenAFullRefreshFindsNewEntities_ThenTheFormerOnesAreRemovedAndTheNewOnesAdded()
    {
        // Arrange
        var wall = new Wall { X = -20, Y = 8 };
        var player = AddPlayer("tab-1");
        AddObject(wall);
        var first = Update(player);
        var formerIds = first.Upserted.Select(view => view.Id).ToHashSet();

        // Act: the index holds new entities of the same subjects without having reported a change.
        _index.Clear();
        var newPlayer = AddPlayer("tab-1");
        AddObject(wall);
        _index.ClearChanges();
        var update = _tracker.Update(_index, _index.EntityChanges.AsSpan(), GameViewWindow.Around(0, 0), newPlayer, isFullRefresh: true,
            _payloads);

        // Assert
        Assert.Equal(formerIds, update.Removed.ToHashSet());
        Assert.Equal(2, update.Upserted.Count);
        Assert.DoesNotContain(update.Upserted, view => formerIds.Contains(view.Id));
        Assert.Equal(2, _tracker.Count);
    }

    [Fact]
    public void WhenAnEntityMovesInTheViewOfTwoPages_ThenBothUpdatesShareItsPayloadWithTheirOwnIdAndSelf()
    {
        // Arrange
        var first = AddPlayer("tab-1");
        var second = AddPlayer("tab-2", x: 10);
        AddObject(new Wall { X = -20, Y = 8 });
        var otherTracker = new GameViewTracker();
        Update(_tracker, first, isLastPage: false);
        var otherFirst = Update(otherTracker, second);

        // Act
        second.Area = second.Area with { X = 11 };
        _index.AddOrUpdate(second);
        var firstUpdate = Update(_tracker, first, isLastPage: false);
        var secondUpdate = Update(otherTracker, second);

        // Assert
        var seenByFirst = Assert.Single(firstUpdate.Upserted);
        var seenBySecond = Assert.Single(secondUpdate.Upserted);
        Assert.Same(seenByFirst.Payload, seenBySecond.Payload);
        Assert.Equal(11, seenByFirst.Payload.X);
        Assert.False(seenByFirst.IsSelf);
        Assert.True(seenBySecond.IsSelf);
        Assert.Equal(Assert.Single(otherFirst.Upserted, view => view.IsSelf).Id, seenBySecond.Id);
    }

    [Fact]
    public void WhenAnEntityChangesOutsideTheWindow_ThenNoPayloadIsBuilt()
    {
        // Arrange
        var player = AddPlayer("tab-1");
        var door = new Door { X = 1000, Y = 0 };
        var entity = AddObject(door);
        Update(player);

        // Act
        door.IsOpen = true;
        entity.ReadFromSubject();
        _index.AddOrUpdate(entity);
        var update = Update(_tracker, player, isLastPage: false);

        // Assert
        Assert.True(update.IsEmpty);
        Assert.Equal(0, _payloads.Count);
    }

    [Fact]
    public void WhenAnAnimalMovesAmongTreesOfItsChunk_ThenOnlyItIsComparedAndUpserted()
    {
        // Arrange
        var player = AddPlayer("tab-1", x: 100);
        for (var i = 0; i < 10; i++)
        {
            AddObject(new VisualObject { X = 2 + i, Y = 4 });
        }

        var animal = AddObject(new VisualObject { X = 5, Y = 6 });
        Update(player, centerX: 100);
        var comparisons = _tracker.ComparisonCount;

        // Act
        animal.Area = animal.Area with { X = 6 };
        _index.AddOrUpdate(animal);
        var update = Update(player, centerX: 100);

        // Assert
        Assert.Equal(6, Assert.Single(update.Upserted).Payload.X);
        Assert.Equal(1, _tracker.ComparisonCount - comparisons);
    }

    [Fact]
    public void WhenAnEntityEntersTheWindowFromOutside_ThenItIsAdded()
    {
        // Arrange
        var player = AddPlayer("tab-1");
        var other = AddPlayer("tab-2", x: 1000);
        Update(player);

        // Act
        other.Area = other.Area with { X = 20 };
        _index.AddOrUpdate(other);
        var update = Update(player);

        // Assert
        var entered = Assert.Single(update.Upserted);
        Assert.Equal((20, false), (entered.Payload.X, entered.IsSelf));
        Assert.Equal(2, _tracker.Count);
    }

    [Fact]
    public void WhenAnEntityIsRemovedAndAddedAgainInOneUpdate_ThenItKeepsItsId()
    {
        // Arrange
        var player = AddPlayer("tab-1");
        var other = AddPlayer("tab-2", x: 10);
        var otherId = Assert.Single(Update(player).Upserted, view => !view.IsSelf).Id;

        // Act
        _index.Remove(other);
        other.Area = other.Area with { X = 12 };
        _index.AddOrUpdate(other);
        var update = Update(player);

        // Assert
        var moved = Assert.Single(update.Upserted);
        Assert.Equal((otherId, 12), (moved.Id, moved.Payload.X));
        Assert.Empty(update.Removed);
    }

    [Fact]
    public void WhenAWallSpanningTheWindowEdgeMovesAlongIt_ThenItStaysKnownOnce()
    {
        // Arrange: the window ends with the chunk of the tiles from 192 to 207.
        var player = AddPlayer("tab-1");
        var wall = new Wall { X = 180, Y = 8, Width = 40 };
        var entity = AddObject(wall);
        var wallId = Assert.Single(Update(player).Upserted, view => !view.IsSelf).Id;

        // Act
        wall.X = 200;
        entity.ReadFromSubject();
        _index.AddOrUpdate(entity);
        var inside = Update(player);
        wall.X = 210;
        entity.ReadFromSubject();
        _index.AddOrUpdate(entity);
        var outside = Update(player);

        // Assert
        Assert.Equal((wallId, 200), (Assert.Single(inside.Upserted).Id, inside.Upserted[0].Payload.X));
        Assert.Empty(inside.Removed);
        Assert.Equal(new[] { wallId }, outside.Removed);
        Assert.Empty(outside.Upserted);
        Assert.Equal(1, _tracker.Count);
    }

    [Fact]
    public void WhenAnObjectMovesWithTheSameVisual_ThenItsPayloadsShareTheParts()
    {
        // Arrange
        var player = AddPlayer("tab-1");
        var entity = AddObject(new VisualObject { X = 5, Y = 5 });
        var first = Assert.Single(Update(player).Upserted, view => !view.IsSelf);

        // Act
        entity.Area = entity.Area with { X = 6 };
        _index.AddOrUpdate(entity);
        var moved = Assert.Single(Update(player).Upserted);

        // Assert
        Assert.NotSame(first.Payload, moved.Payload);
        Assert.Same(first.Payload.Parts, moved.Payload.Parts);
    }
}

