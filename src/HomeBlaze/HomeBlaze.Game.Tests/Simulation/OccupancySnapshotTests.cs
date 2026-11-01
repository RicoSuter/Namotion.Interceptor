using HomeBlaze.Game.Simulation;
using HomeBlaze.Game.Tests.Testing;
using HomeBlaze.Game.World;

namespace HomeBlaze.Game.Tests.Simulation;

public class OccupancySnapshotTests
{
    [Fact]
    public void WhenTheSnapshotIsEmpty_ThenEveryAreaIsFree()
    {
        // Act
        var isFree = OccupancySnapshot.Empty.IsFree(new GameArea(-100, -100, 200, 200), except: null);

        // Assert
        Assert.True(isFree);
    }

    [Fact]
    public void WhenAChangedChunkHoldsABlockingEntity_ThenOnlyItsAreaIsOccupiedExceptForItsOwnSubject()
    {
        // Arrange
        var index = new GameEntityIndex();
        var entity = CreateEntity(new GameArea(14, 0, 4, 1), isMovementBlocking: true);
        index.AddOrUpdate(entity);

        // Act
        var snapshot = OccupancySnapshot.Empty.With(index, index.Changes);

        // Assert
        Assert.False(snapshot.IsFree(new GameArea(16, 0, 1, 1), except: null));
        Assert.False(snapshot.IsFree(new GameArea(13, -1, 2, 2), except: null));
        Assert.True(snapshot.IsFree(new GameArea(16, 0, 1, 1), except: entity.Subject));
        Assert.True(snapshot.IsFree(new GameArea(14, 1, 4, 1), except: null));
    }

    [Fact]
    public void WhenAChangedChunkHoldsOnlyNonBlockingEntities_ThenItIsFree()
    {
        // Arrange
        var index = new GameEntityIndex();
        index.AddOrUpdate(CreateEntity(new GameArea(0, 0, 2, 2), isMovementBlocking: false));

        // Act
        var snapshot = OccupancySnapshot.Empty.With(index, index.Changes);

        // Assert
        Assert.True(snapshot.IsFree(new GameArea(0, 0, 2, 2), except: null));
    }

    [Fact]
    public void WhenABlockingEntityMovesAway_ThenTheNextSnapshotIsFreeThereAndThePreviousOneStaysOccupied()
    {
        // Arrange
        var index = new GameEntityIndex();
        var entity = CreateEntity(new GameArea(0, 0, 2, 2), isMovementBlocking: true);
        index.AddOrUpdate(entity);
        var previous = OccupancySnapshot.Empty.With(index, index.Changes);
        index.Changes.Clear();

        // Act
        entity.Area = new GameArea(40, 0, 2, 2);
        index.AddOrUpdate(entity);
        var next = previous.With(index, index.Changes);

        // Assert
        Assert.True(next.IsFree(new GameArea(0, 0, 2, 2), except: null));
        Assert.False(next.IsFree(new GameArea(40, 0, 2, 2), except: null));
        Assert.False(previous.IsFree(new GameArea(0, 0, 2, 2), except: null));
        Assert.True(previous.IsFree(new GameArea(40, 0, 2, 2), except: null));
    }

    [Fact]
    public void WhenNothingChanged_ThenTheSameSnapshotIsReturned()
    {
        // Arrange
        var index = new GameEntityIndex();
        index.AddOrUpdate(CreateEntity(new GameArea(0, 0, 2, 2), isMovementBlocking: true));
        var snapshot = OccupancySnapshot.Empty.With(index, index.Changes);
        index.Changes.Clear();

        // Act
        var next = snapshot.With(index, index.Changes);

        // Assert
        Assert.Same(snapshot, next);
    }

    [Fact]
    public void WhenAnUnchangedChunkHoldsABlockingEntity_ThenTheNextSnapshotKeepsIt()
    {
        // Arrange
        var index = new GameEntityIndex();
        index.AddOrUpdate(CreateEntity(new GameArea(0, 0, 2, 2), isMovementBlocking: true));
        var snapshot = OccupancySnapshot.Empty.With(index, index.Changes);
        index.Changes.Clear();
        index.AddOrUpdate(CreateEntity(new GameArea(100, 100, 2, 2), isMovementBlocking: true));

        // Act
        var next = snapshot.With(index, index.Changes);

        // Assert
        Assert.False(next.IsFree(new GameArea(0, 0, 1, 1), except: null));
        Assert.False(next.IsFree(new GameArea(100, 100, 1, 1), except: null));
    }

    [Fact]
    public void WhenAnEntityChangedButTheIndexWasNotUpdated_ThenTheSnapshotKeepsTheStoredArea()
    {
        // Arrange
        var index = new GameEntityIndex();
        var entity = CreateEntity(new GameArea(0, 0, 2, 2), isMovementBlocking: true);
        index.AddOrUpdate(entity);

        // Act
        entity.Area = new GameArea(4, 4, 2, 2);
        entity.IsMovementBlocking = false;
        var snapshot = OccupancySnapshot.Empty.With(index, index.Changes);

        // Assert
        Assert.False(snapshot.IsFree(new GameArea(0, 0, 2, 2), except: null));
        Assert.True(snapshot.IsFree(new GameArea(4, 4, 2, 2), except: null));
    }

    [Fact]
    public void WhenOnlyTheVisualOrANonBlockingEntityChanged_ThenTheSameSnapshotIsReturned()
    {
        // Arrange
        var index = new GameEntityIndex();
        var blocking = CreateEntity(new GameArea(0, 0, 2, 2), isMovementBlocking: true);
        var walker = CreateEntity(new GameArea(4, 0, 2, 2), isMovementBlocking: false);
        index.AddOrUpdate(blocking);
        index.AddOrUpdate(walker);
        var snapshot = OccupancySnapshot.Empty.With(index, index.Changes);
        index.Changes.Clear();

        // Act
        blocking.Visual = GameVisual.Box(1, 2, 1);
        index.AddOrUpdate(blocking);
        walker.Area = new GameArea(40, 0, 2, 2);
        index.AddOrUpdate(walker);
        var next = snapshot.With(index, index.Changes);

        // Assert
        Assert.NotEqual(0, index.Changes.Count);
        Assert.Same(snapshot, next);
    }

    private static GameEntity CreateEntity(GameArea area, bool isMovementBlocking) =>
        new(new MovementBlocker()) { Area = area, Height = 1, IsMovementBlocking = isMovementBlocking, IsVisible = true };
}
