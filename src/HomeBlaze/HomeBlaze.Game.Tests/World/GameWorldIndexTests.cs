using HomeBlaze.Game.Tests.Testing;
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

    [Fact]
    public void WhenAWallOrAClosedDoorCoversATile_ThenTheTileBlocksSound()
    {
        // Arrange
        var index = new GameWorldIndex();
        index.AddOrUpdate(new Wall { X = 0, Y = 0, Width = 4, Depth = 1 });
        index.AddOrUpdate(new Door { X = 10, Y = 0 });

        // Act
        var isWallBlocked = index.IsSoundBlocked(3, 0);
        var isDoorBlocked = index.IsSoundBlocked(11, 0);

        // Assert
        Assert.True(isWallBlocked);
        Assert.True(isDoorBlocked);
    }

    [Fact]
    public void WhenAnOpenDoorAPlayerOrNothingCoversATile_ThenTheTileLetsSoundThrough()
    {
        // Arrange
        var index = new GameWorldIndex();
        index.AddOrUpdate(new Door { X = 0, Y = 0, IsOpen = true });
        index.AddOrUpdate(new GamePlayer { X = 10, Y = 0, IsConnected = true });

        // Act
        var isOpenDoorBlocked = index.IsSoundBlocked(1, 0);
        var isPlayerBlocked = index.IsSoundBlocked(10, 0);
        var isEmptyBlocked = index.IsSoundBlocked(50, 50);

        // Assert
        Assert.False(isOpenDoorBlocked);
        Assert.False(isPlayerBlocked);
        Assert.False(isEmptyBlocked);
    }

    [Fact]
    public void WhenAnObjectBlocksMovementButNotSound_ThenItsTilesAreBlockedButLetSoundThrough()
    {
        // Arrange
        var index = new GameWorldIndex();
        index.AddOrUpdate(new MovementBlocker { X = 0, Y = 0, Width = 2, Depth = 2 });

        // Act
        var isBlocked = index.IsBlocked(new Footprint(1, 1, 1, 1), ignored: null);
        var isSoundBlocked = index.IsSoundBlocked(1, 1);

        // Assert
        Assert.True(isBlocked);
        Assert.False(isSoundBlocked);
    }

    [Fact]
    public void WhenADoorOpensAndCloses_ThenItsSoundBlockingFollowsAndTheVersionIncreases()
    {
        // Arrange
        var index = new GameWorldIndex();
        var door = new Door { X = 0, Y = 0 };
        index.AddOrUpdate(door);
        var closedVersion = index.Version;
        var isClosedBlocked = index.IsSoundBlocked(1, 0);

        // Act
        door.IsOpen = true;
        index.AddOrUpdate(door);
        var openVersion = index.Version;
        var isOpenBlocked = index.IsSoundBlocked(1, 0);
        door.IsOpen = false;
        index.AddOrUpdate(door);

        // Assert
        Assert.True(isClosedBlocked);
        Assert.False(isOpenBlocked);
        Assert.True(index.IsSoundBlocked(1, 0));
        Assert.True(openVersion > closedVersion);
        Assert.True(index.Version > openVersion);
    }

    [Fact]
    public void WhenTheSoundBlockingObjectsOfAnAreaAreQueried_ThenOnlyWallsAndClosedDoorsAreFoundOnce()
    {
        // Arrange
        var index = new GameWorldIndex();
        var wall = new Wall { X = 14, Y = 0, Width = 4, Depth = 1 };
        var closedDoor = new Door { X = 0, Y = 4 };
        index.AddOrUpdate(wall);
        index.AddOrUpdate(closedDoor);
        index.AddOrUpdate(new Door { X = 0, Y = 8, IsOpen = true });
        index.AddOrUpdate(new MovementBlocker { X = 8, Y = 8 });
        index.AddOrUpdate(new GamePlayer { X = 10, Y = 10, IsConnected = true });
        index.AddOrUpdate(new Wall { X = 100, Y = 100 });
        var results = new List<Footprint>();

        // Act
        index.QuerySoundBlocking(new Footprint(0, 0, 20, 20), results);

        // Assert
        Assert.Equal(2, results.Count);
        Assert.Contains(wall.Footprint, results);
        Assert.Contains(closedDoor.Footprint, results);
    }

    [Fact]
    public void WhenObjectsAreAddedMovedRemovedAndCleared_ThenTheVersionIncreasesEachTime()
    {
        // Arrange
        var index = new GameWorldIndex();
        var wall = new Wall();
        var versions = new List<long> { index.Version };

        // Act
        index.AddOrUpdate(wall);
        versions.Add(index.Version);
        wall.X = 10;
        index.AddOrUpdate(wall);
        versions.Add(index.Version);
        index.Remove(wall);
        versions.Add(index.Version);
        index.Clear();
        versions.Add(index.Version);

        // Assert
        Assert.All(versions.Zip(versions.Skip(1)), pair => Assert.True(pair.Second > pair.First));
    }

    [Fact]
    public void WhenAnUnchangedObjectIsUpdatedOrAnUnknownOneRemoved_ThenTheVersionStays()
    {
        // Arrange
        var index = new GameWorldIndex();
        var wall = new Wall();
        index.AddOrUpdate(wall);
        var version = index.Version;

        // Act
        index.AddOrUpdate(wall);
        index.Remove(new Wall());

        // Assert
        Assert.Equal(version, index.Version);
    }
}
