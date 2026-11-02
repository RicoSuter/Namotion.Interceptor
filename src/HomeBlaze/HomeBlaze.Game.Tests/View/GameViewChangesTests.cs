using HomeBlaze.Game.Tests.Testing;
using HomeBlaze.Game.View;
using HomeBlaze.Game.World;

namespace HomeBlaze.Game.Tests.View;

public class GameViewChangesTests
{
    private readonly GameWorldIndex _index = new();
    private readonly GameViewChanges _changes = new();

    [Fact]
    public void WhenEntitiesChangeNearAndFarFromTheWindow_ThenOnlyTheNearOnesAreCollected()
    {
        // Arrange
        var near = EntityFactory.Player("tab-1", 10, 0);
        var far = EntityFactory.Player("tab-2", 5000, 0);
        var leaving = EntityFactory.Player("tab-3", 20, 0);
        _index.AddOrUpdate(near);
        _index.AddOrUpdate(far);
        _index.AddOrUpdate(leaving);
        _index.ClearChanges();

        // Act
        near.Area = near.Area with { X = 11 };
        _index.AddOrUpdate(near);
        far.Area = far.Area with { X = 5001 };
        _index.AddOrUpdate(far);
        leaving.Area = leaving.Area with { X = 6000 };
        _index.AddOrUpdate(leaving);
        var collected = _changes.Collect(_index, GameViewWindow.Around(0, 0)).ToArray();

        // Assert
        Assert.Equal(2, collected.Length);
        Assert.Contains(collected, change => ReferenceEquals(change.Entity, near));
        Assert.Contains(collected, change => ReferenceEquals(change.Entity, leaving) && change.OldArea.X == 20);
    }

    [Fact]
    public void WhenAnEntitySpansSeveralBucketsOfTheWindow_ThenItIsCollectedOnce()
    {
        // Arrange: the buckets are 256 tiles wide, so the wall covers two of them.
        var wall = EntityFactory.Object(new Wall { X = 250, Y = 0, Width = 20 });
        _index.AddOrUpdate(wall);

        // Act
        var collected = _changes.Collect(_index, GameViewWindow.Around(256, 0)).ToArray();

        // Assert
        Assert.Same(wall, Assert.Single(collected).Entity);
    }

    [Fact]
    public void WhenTheChangesAreCleared_ThenTheNextCollectionSortsTheNewOnes()
    {
        // Arrange
        var player = EntityFactory.Player("tab-1", 10, 0);
        _index.AddOrUpdate(player);
        _changes.Collect(_index, GameViewWindow.Around(0, 0));
        _index.ClearChanges();
        _changes.Clear();

        // Act
        var empty = _changes.Collect(_index, GameViewWindow.Around(0, 0)).Length;
        player.Area = player.Area with { X = 12 };
        _index.AddOrUpdate(player);
        var collected = _changes.Collect(_index, GameViewWindow.Around(0, 0)).ToArray();

        // Assert
        Assert.Equal(0, empty);
        Assert.Same(player, Assert.Single(collected).Entity);
    }
}
