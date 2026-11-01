using HomeBlaze.Game.Simulation;
using HomeBlaze.Game.Tests.Testing;
using HomeBlaze.Game.World;

namespace HomeBlaze.Game.Tests.World;

public class GameEntityIndexTests
{
    [Fact]
    public void WhenABlockingEntityCoversATileOfTheArea_ThenTheAreaIsBlocked()
    {
        // Arrange
        var index = new GameEntityIndex();
        index.AddOrUpdate(CreateEntity(new GameArea(0, 2, 4, 1), isMovementBlocking: true));

        // Act
        var isBlocked = index.IsBlocked(new GameArea(3, 1, 2, 2), ignored: null);

        // Assert
        Assert.True(isBlocked);
    }

    [Fact]
    public void WhenOnlyTheIgnoredEntityCoversTheArea_ThenTheAreaIsFree()
    {
        // Arrange
        var index = new GameEntityIndex();
        var player = CreateEntity(new GameArea(0, 0, 2, 2), isMovementBlocking: true);
        index.AddOrUpdate(player);

        // Act
        var isBlocked = index.IsBlocked(new GameArea(1, 1, 2, 2), ignored: player);

        // Assert
        Assert.False(isBlocked);
    }

    [Fact]
    public void WhenAnEntityMoves_ThenItsOldTilesAreFreeAndItsNewTilesBlocked()
    {
        // Arrange
        var index = new GameEntityIndex();
        var entity = CreateEntity(new GameArea(0, 0, 4, 1), isMovementBlocking: true);
        index.AddOrUpdate(entity);

        // Act
        entity.Area = new GameArea(100, 0, 4, 1);
        index.AddOrUpdate(entity);

        // Assert
        Assert.False(index.IsBlocked(new GameArea(0, 0, 1, 1), ignored: null));
        Assert.True(index.IsBlocked(new GameArea(103, 0, 1, 1), ignored: null));
    }

    [Fact]
    public void WhenAnEntitySpansSeveralChunks_ThenQueryReturnsItOnce()
    {
        // Arrange
        var index = new GameEntityIndex();
        var entity = CreateEntity(new GameArea(-40, -1, 80, 1));
        index.AddOrUpdate(entity);
        var results = new List<GameEntity>();

        // Act
        index.Query(new GameArea(-50, -50, 100, 100), results);

        // Assert
        Assert.Same(entity, Assert.Single(results));
    }

    [Fact]
    public void WhenAnEntityIsRemoved_ThenQueryDoesNotReturnIt()
    {
        // Arrange
        var index = new GameEntityIndex();
        var entity = CreateEntity(new GameArea(0, 0, 1, 1));
        index.AddOrUpdate(entity);
        var results = new List<GameEntity>();

        // Act
        index.Remove(entity);
        index.Query(new GameArea(-10, -10, 20, 20), results);

        // Assert
        Assert.Empty(results);
        Assert.Equal(0, index.Count);
    }

    [Fact]
    public void WhenAnEntityIsNeitherBlockingNorVisible_ThenItNeitherBlocksNorIsReturned()
    {
        // Arrange
        var index = new GameEntityIndex();
        index.AddOrUpdate(CreateEntity(new GameArea(0, 0, 2, 2), isVisible: false));
        var results = new List<GameEntity>();

        // Act
        var isBlocked = index.IsBlocked(new GameArea(0, 0, 2, 2), ignored: null);
        index.Query(new GameArea(0, 0, 2, 2), results);

        // Assert
        Assert.False(isBlocked);
        Assert.Empty(results);
    }

    [Fact]
    public void WhenEntitiesStandAtNegativeCoordinates_ThenQueryFindsOnlyTheIntersectingOnes()
    {
        // Arrange
        var index = new GameEntityIndex();
        var near = CreateEntity(new GameArea(-17, -17, 1, 1));
        var far = CreateEntity(new GameArea(-300, -300, 4, 1));
        index.AddOrUpdate(near);
        index.AddOrUpdate(far);
        var results = new List<GameEntity>();

        // Act
        index.Query(new GameArea(-20, -20, 10, 10), results);

        // Assert
        Assert.Same(near, Assert.Single(results));
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(GameObject.MaximumSize + 1, 1, 1)]
    [InlineData(1, -4, 1)]
    [InlineData(1, 1, 0)]
    public void WhenAnEntityHasASizeOutsideTheRange_ThenItNeitherBlocksNorIsReturned(int width, int depth, int height)
    {
        // Arrange
        var index = new GameEntityIndex();
        var entity = CreateEntity(new GameArea(0, 0, width, depth), isMovementBlocking: true, height: height);
        var results = new List<GameEntity>();

        // Act
        index.AddOrUpdate(entity);
        index.Query(new GameArea(-10, -10, 20, 20), results);

        // Assert
        Assert.False(index.IsBlocked(new GameArea(0, 0, 1, 1), ignored: null));
        Assert.Empty(results);
        Assert.Equal(0, index.Count);
    }

    [Fact]
    public void WhenAnIndexedEntityGetsASizeOutsideTheRange_ThenItIsDroppedAndItsChunksAreChanged()
    {
        // Arrange
        var index = new GameEntityIndex();
        var entity = CreateEntity(new GameArea(0, 0, 4, 1), isMovementBlocking: true);
        index.AddOrUpdate(entity);
        index.Changes.Clear();

        // Act
        entity.Area = entity.Area with { Width = 0 };
        index.AddOrUpdate(entity);

        // Assert
        Assert.False(index.IsBlocked(new GameArea(0, 0, 1, 1), ignored: null));
        Assert.Equal(0, index.Count);
        Assert.True(index.Changes.Contains((0, 0)));
    }

    [Fact]
    public void WhenAWallOrAClosedDoorIsReadIntoTheIndex_ThenItsTilesBlockSound()
    {
        // Arrange
        var index = new GameEntityIndex();
        index.AddOrUpdate(ReadEntity(new Wall { X = 0, Y = 0, Width = 4, Depth = 1 }));
        index.AddOrUpdate(ReadEntity(new Door { X = 10, Y = 0 }));

        // Act
        var isWallBlocked = index.IsSoundBlocked(3, 0);
        var isDoorBlocked = index.IsSoundBlocked(11, 0);

        // Assert
        Assert.True(isWallBlocked);
        Assert.True(isDoorBlocked);
    }

    [Fact]
    public void WhenAnOpenDoorABlockingEntityOrNothingCoversATile_ThenTheTileLetsSoundThrough()
    {
        // Arrange
        var index = new GameEntityIndex();
        index.AddOrUpdate(ReadEntity(new Door { X = 0, Y = 0, IsOpen = true }));
        index.AddOrUpdate(CreateEntity(new GameArea(10, 0, 2, 2), isMovementBlocking: true));

        // Act
        var isOpenDoorBlocked = index.IsSoundBlocked(1, 0);
        var isBlockerBlocked = index.IsSoundBlocked(10, 0);
        var isEmptyBlocked = index.IsSoundBlocked(50, 50);

        // Assert
        Assert.False(isOpenDoorBlocked);
        Assert.False(isBlockerBlocked);
        Assert.False(isEmptyBlocked);
    }

    [Fact]
    public void WhenADoorOpensAndCloses_ThenItsSoundBlockingFollowsAndTheVersionIncreases()
    {
        // Arrange
        var index = new GameEntityIndex();
        var door = new Door { X = 0, Y = 0 };
        var entity = ReadEntity(door);
        index.AddOrUpdate(entity);
        var closedVersion = index.Version;
        var isClosedBlocked = index.IsSoundBlocked(1, 0);

        // Act
        door.IsOpen = true;
        entity.ReadFromSubject();
        index.AddOrUpdate(entity);
        var openVersion = index.Version;
        var isOpenBlocked = index.IsSoundBlocked(1, 0);
        door.IsOpen = false;
        entity.ReadFromSubject();
        index.AddOrUpdate(entity);

        // Assert
        Assert.True(isClosedBlocked);
        Assert.False(isOpenBlocked);
        Assert.True(index.IsSoundBlocked(1, 0));
        Assert.True(openVersion > closedVersion);
        Assert.True(index.Version > openVersion);
    }

    [Fact]
    public void WhenTheSoundBlockingEntitiesOfAnAreaAreQueried_ThenOnlySoundBlockingOnesAreFoundOnce()
    {
        // Arrange
        var index = new GameEntityIndex();
        var wall = CreateEntity(new GameArea(14, 0, 4, 1), isMovementBlocking: true, isSoundBlocking: true);
        var closedDoor = CreateEntity(new GameArea(0, 4, 4, 1), isMovementBlocking: true, isSoundBlocking: true);
        index.AddOrUpdate(wall);
        index.AddOrUpdate(closedDoor);
        index.AddOrUpdate(CreateEntity(new GameArea(0, 8, 4, 1)));
        index.AddOrUpdate(CreateEntity(new GameArea(8, 8, 1, 1), isMovementBlocking: true));
        index.AddOrUpdate(CreateEntity(new GameArea(100, 100, 4, 1), isMovementBlocking: true, isSoundBlocking: true));
        var results = new List<GameArea>();

        // Act
        index.QuerySoundBlocking(new GameArea(0, 0, 20, 20), results);

        // Assert
        Assert.Equal(2, results.Count);
        Assert.Contains(wall.Area, results);
        Assert.Contains(closedDoor.Area, results);
    }

    [Fact]
    public void WhenEntitiesAreAddedMovedRemovedAndCleared_ThenTheVersionIncreasesEachTime()
    {
        // Arrange
        var index = new GameEntityIndex();
        var entity = CreateEntity(new GameArea(0, 0, 1, 1));
        var versions = new List<long> { index.Version };

        // Act
        index.AddOrUpdate(entity);
        versions.Add(index.Version);
        entity.Area = new GameArea(10, 0, 1, 1);
        index.AddOrUpdate(entity);
        versions.Add(index.Version);
        index.Remove(entity);
        versions.Add(index.Version);
        index.Clear();
        versions.Add(index.Version);

        // Assert
        Assert.All(versions.Zip(versions.Skip(1)), pair => Assert.True(pair.Second > pair.First));
    }

    [Fact]
    public void WhenAnUnchangedEntityIsUpdatedOrAnUnknownOneRemoved_ThenNeitherTheVersionNorTheChangesMove()
    {
        // Arrange
        var index = new GameEntityIndex();
        var entity = CreateEntity(new GameArea(0, 0, 1, 1));
        index.AddOrUpdate(entity);
        index.Changes.Clear();
        var version = index.Version;

        // Act
        index.AddOrUpdate(entity);
        index.Remove(CreateEntity(new GameArea(0, 0, 1, 1)));

        // Assert
        Assert.Equal(version, index.Version);
        Assert.Equal(0, index.Changes.Count);
    }

    [Fact]
    public void WhenAWallGrowsToTwoMeters_ThenItsTilesBlockSound()
    {
        // Arrange
        var index = new GameEntityIndex();
        var wall = new Wall { X = 0, Y = 0, Height = 4 };
        var entity = ReadEntity(wall);
        index.AddOrUpdate(entity);
        var isBlockedBefore = index.IsSoundBlocked(0, 0);
        var versionBefore = index.Version;

        // Act
        wall.Height = 8;
        entity.ReadFromSubject();
        index.AddOrUpdate(entity);

        // Assert
        Assert.False(isBlockedBefore);
        Assert.True(index.IsSoundBlocked(0, 0));
        Assert.True(index.Version > versionBefore);
    }

    [Fact]
    public void WhenAnEntityMovesToAnotherChunk_ThenBothChunksAreChanged()
    {
        // Arrange
        var index = new GameEntityIndex();
        var entity = CreateEntity(new GameArea(0, 0, 2, 2), isMovementBlocking: true);
        index.AddOrUpdate(entity);
        index.Changes.Clear();

        // Act
        entity.Area = new GameArea(16, 0, 2, 2);
        index.AddOrUpdate(entity);

        // Assert
        Assert.True(index.Changes.Contains((0, 0)));
        Assert.True(index.Changes.Contains((1, 0)));
    }

    [Fact]
    public void WhenAnEntityIsAddedOrRemoved_ThenOnlyTheChunksOfItsAreaAreChanged()
    {
        // Arrange
        var index = new GameEntityIndex();
        var entity = CreateEntity(new GameArea(15, -1, 2, 2));

        // Act
        index.AddOrUpdate(entity);
        var addedChunks = index.Changes.Chunks.ToHashSet();
        index.Changes.Clear();
        index.Remove(entity);
        var removedChunks = index.Changes.Chunks.ToHashSet();

        // Assert
        HashSet<(long X, long Y)> expected = [(0, -1), (1, -1), (0, 0), (1, 0)];
        Assert.Equal(expected, addedChunks);
        Assert.Equal(expected, removedChunks);
    }

    [Fact]
    public void WhenOnlyTheVisualOrTitleChanges_ThenTheChunksAreChangedButTheVersionStays()
    {
        // Arrange
        var index = new GameEntityIndex();
        var entity = CreateEntity(new GameArea(0, 0, 1, 1), isMovementBlocking: true);
        index.AddOrUpdate(entity);
        index.Changes.Clear();
        var version = index.Version;

        // Act
        entity.Visual = GameVisual.Box(1, 2, 1);
        index.AddOrUpdate(entity);
        var changesAfterVisual = index.Changes.Count;
        index.Changes.Clear();
        entity.Title = "Renamed";
        index.AddOrUpdate(entity);

        // Assert
        Assert.Equal(1, changesAfterVisual);
        Assert.True(index.Changes.Contains((0, 0)));
        Assert.Equal(version, index.Version);
        Assert.True(index.IsBlocked(new GameArea(0, 0, 1, 1), ignored: null));
    }

    [Fact]
    public void WhenAMovementBlockingEntityAlreadyOverlapsThePlayer_ThenItDoesNotBlockTheStepOut()
    {
        // Arrange
        var index = new GameEntityIndex();
        var door = CreateEntity(new GameArea(0, 0, 4, 1), isMovementBlocking: true);
        var player = CreateEntity(new GameArea(1, 0, 2, 2), isMovementBlocking: true);
        index.AddOrUpdate(door);
        index.AddOrUpdate(player);

        // Act
        var isBlocked = index.IsBlockedExceptOverlapping(new GameArea(1, 1, 2, 2), player);
        var isSidewaysBlocked = index.IsBlockedExceptOverlapping(new GameArea(0, 0, 2, 2), player);

        // Assert
        Assert.False(isBlocked);
        Assert.False(isSidewaysBlocked);
        Assert.True(index.IsBlocked(new GameArea(0, 0, 2, 2), ignored: player));
    }

    [Fact]
    public void WhenAMovementBlockingEntityDoesNotOverlapThePlayer_ThenItBlocksTheStep()
    {
        // Arrange
        var index = new GameEntityIndex();
        var door = CreateEntity(new GameArea(0, 0, 4, 1), isMovementBlocking: true);
        var wall = CreateEntity(new GameArea(0, 3, 4, 1), isMovementBlocking: true);
        var player = CreateEntity(new GameArea(1, 0, 2, 2), isMovementBlocking: true);
        index.AddOrUpdate(door);
        index.AddOrUpdate(wall);
        index.AddOrUpdate(player);

        // Act
        var isBlocked = index.IsBlockedExceptOverlapping(new GameArea(1, 2, 2, 2), player);

        // Assert
        Assert.True(isBlocked);
    }

    [Fact]
    public void WhenAChunkIsQueried_ThenEveryEntityCoveringItIsReturnedOnce()
    {
        // Arrange
        var index = new GameEntityIndex();
        var blocking = CreateEntity(new GameArea(0, 0, 1, 1), isMovementBlocking: true);
        var hidden = CreateEntity(new GameArea(5, 5, 1, 1), isVisible: false);
        var spanning = CreateEntity(new GameArea(-20, 2, 40, 1));
        var elsewhere = CreateEntity(new GameArea(16, 0, 1, 1));
        index.AddOrUpdate(blocking);
        index.AddOrUpdate(hidden);
        index.AddOrUpdate(spanning);
        index.AddOrUpdate(elsewhere);
        var results = new List<GameEntity>();

        // Act
        index.QueryChunk((0, 0), results);

        // Assert
        Assert.Equal(3, results.Count);
        Assert.Contains(blocking, results);
        Assert.Contains(hidden, results);
        Assert.Contains(spanning, results);
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(15, 16, 0, 1)]
    [InlineData(-1, -16, -1, -1)]
    [InlineData(-17, 31, -2, 1)]
    public void WhenATileIsMappedToItsChunk_ThenChunksAreSixteenTilesWide(long x, long y, long chunkX, long chunkY)
    {
        // Act
        var chunk = GameEntityIndex.GetChunk(x, y);

        // Assert
        Assert.Equal((chunkX, chunkY), chunk);
    }

    [Fact]
    public void WhenTheIndexIsCleared_ThenTheChunksOfAllEntitiesAreChanged()
    {
        // Arrange
        var index = new GameEntityIndex();
        index.AddOrUpdate(CreateEntity(new GameArea(0, 0, 1, 1)));
        index.AddOrUpdate(CreateEntity(new GameArea(40, 40, 1, 1)));
        index.Changes.Clear();

        // Act
        index.Clear();

        // Assert
        Assert.Equal(0, index.Count);
        Assert.True(index.Changes.Contains((0, 0)));
        Assert.True(index.Changes.Contains((2, 2)));
    }

    [Theory]
    [InlineData(1, 1, 1, true)]
    [InlineData(GameObject.MaximumSize, GameObject.MaximumSize, GameObject.MaximumSize, true)]
    [InlineData(0, 1, 1, false)]
    [InlineData(1, GameObject.MaximumSize + 1, 1, false)]
    [InlineData(1, 1, -1, false)]
    public void WhenTheSizeIsChecked_ThenOnlyOneToTheMaximumIsValid(int width, int depth, int height, bool expected)
    {
        // Act
        var isValid = GameEntityIndex.HasValidSize(new GameArea(0, 0, width, depth), height);

        // Assert
        Assert.Equal(expected, isValid);
    }

    private static GameEntity CreateEntity(
        GameArea area, bool isMovementBlocking = false, bool isSoundBlocking = false, bool isVisible = true, int height = 1) =>
        new(new MovementBlocker())
        {
            Area = area,
            Height = height,
            IsMovementBlocking = isMovementBlocking,
            IsSoundBlocking = isSoundBlocking,
            IsVisible = isVisible
        };

    private static GameEntity ReadEntity(GameObject subject)
    {
        var entity = new GameEntity(subject);
        entity.ReadFromSubject();
        return entity;
    }
}
