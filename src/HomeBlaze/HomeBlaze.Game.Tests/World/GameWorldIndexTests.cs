using HomeBlaze.Game.World;

namespace HomeBlaze.Game.Tests.World;

public class GameWorldIndexTests
{
    [Fact]
    public void WhenWallCoversATileOfTheArea_ThenTheAreaIsBlocked()
    {
        // Arrange
        var index = new GameWorldIndex();
        index.AddOrUpdate(new Wall { X = 0, Y = 2, Width = 4, Depth = 1 });

        // Act
        var isBlocked = index.IsBlocked(new Footprint(3, 1, 2, 2), ignored: null);

        // Assert
        Assert.True(isBlocked);
    }

    [Fact]
    public void WhenOnlyTheIgnoredObjectCoversTheArea_ThenTheAreaIsFree()
    {
        // Arrange
        var index = new GameWorldIndex();
        var player = new GamePlayer { IsConnected = true };
        index.AddOrUpdate(player);

        // Act
        var isBlocked = index.IsBlocked(new Footprint(1, 1, 2, 2), ignored: player);

        // Assert
        Assert.False(isBlocked);
    }

    [Fact]
    public void WhenObjectMoves_ThenItsOldTilesAreFreeAndItsNewTilesBlocked()
    {
        // Arrange
        var index = new GameWorldIndex();
        var wall = new Wall { X = 0, Y = 0 };
        index.AddOrUpdate(wall);

        // Act
        wall.X = 100;
        index.AddOrUpdate(wall);

        // Assert
        Assert.False(index.IsBlocked(new Footprint(0, 0, 1, 1), ignored: null));
        Assert.True(index.IsBlocked(new Footprint(103, 0, 1, 1), ignored: null));
    }

    [Fact]
    public void WhenObjectSpansSeveralChunks_ThenQueryReturnsItOnce()
    {
        // Arrange
        var index = new GameWorldIndex();
        var wall = new Wall { X = -40, Y = -1, Width = 80, Depth = 1 };
        index.AddOrUpdate(wall);
        var results = new List<GameObject>();

        // Act
        index.Query(new Footprint(-50, -50, 100, 100), results);

        // Assert
        Assert.Same(wall, Assert.Single(results));
    }

    [Fact]
    public void WhenObjectIsRemoved_ThenQueryDoesNotReturnIt()
    {
        // Arrange
        var index = new GameWorldIndex();
        var wall = new Wall();
        index.AddOrUpdate(wall);
        var results = new List<GameObject>();

        // Act
        index.Remove(wall);
        index.Query(new Footprint(-10, -10, 20, 20), results);

        // Assert
        Assert.Empty(results);
        Assert.Equal(0, index.Count);
    }

    [Fact]
    public void WhenPlayerIsDisconnected_ThenItNeitherBlocksNorIsReturned()
    {
        // Arrange
        var index = new GameWorldIndex();
        index.AddOrUpdate(new GamePlayer { IsConnected = false });
        var results = new List<GameObject>();

        // Act
        var isBlocked = index.IsBlocked(new Footprint(0, 0, 2, 2), ignored: null);
        index.Query(new Footprint(0, 0, 2, 2), results);

        // Assert
        Assert.False(isBlocked);
        Assert.Empty(results);
    }

    [Fact]
    public void WhenObjectsStandAtNegativeCoordinates_ThenQueryFindsOnlyTheIntersectingOnes()
    {
        // Arrange
        var index = new GameWorldIndex();
        var near = new Wall { X = -17, Y = -17, Width = 1 };
        var far = new Wall { X = -300, Y = -300 };
        index.AddOrUpdate(near);
        index.AddOrUpdate(far);
        var results = new List<GameObject>();

        // Act
        index.Query(new Footprint(-20, -20, 10, 10), results);

        // Assert
        Assert.Same(near, Assert.Single(results));
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(GameObject.MaximumSize + 1, 1, 1)]
    [InlineData(1, -4, 1)]
    [InlineData(1, 1, 0)]
    public void WhenAnObjectHasASizeOutsideTheRange_ThenItNeitherBlocksNorIsReturned(int width, int depth, int height)
    {
        // Arrange
        var index = new GameWorldIndex();
        var wall = new Wall { X = 0, Y = 0, Width = width, Depth = depth, Height = height };
        var results = new List<GameObject>();

        // Act
        index.AddOrUpdate(wall);
        index.Query(new Footprint(-10, -10, 20, 20), results);

        // Assert
        Assert.False(index.IsBlocked(new Footprint(0, 0, 1, 1), ignored: null));
        Assert.Empty(results);
        Assert.Equal(0, index.Count);
    }

    [Fact]
    public void WhenAnIndexedObjectGetsASizeOutsideTheRange_ThenItIsDropped()
    {
        // Arrange
        var index = new GameWorldIndex();
        var wall = new Wall { X = 0, Y = 0 };
        index.AddOrUpdate(wall);

        // Act
        wall.Width = 0;
        index.AddOrUpdate(wall);

        // Assert
        Assert.False(index.IsBlocked(new Footprint(0, 0, 1, 1), ignored: null));
        Assert.Equal(0, index.Count);
    }
}
