using HomeBlaze.Game.Simulation;
using HomeBlaze.Game.Tests.Testing;
using HomeBlaze.Game.World;

namespace HomeBlaze.Game.Tests.World;

public class SoundPathSearchTests
{
    [Theory]
    [InlineData(GameObject.MaximumSize + 1, 2)]
    [InlineData(2, 0)]
    public void WhenTheSourceHasAnInvalidSize_ThenNoTargetIsReached(int width, int depth)
    {
        // Arrange
        var search = new SoundPathSearch();

        // Act
        search.Prepare(new GameArea(0, 0, width, depth), new SoundMap());
        var length = search.GetLength(new GameArea(4, 0, 2, 2));

        // Assert
        Assert.Equal(SoundPathSearch.NoPath, length);
    }

    [Fact]
    public void WhenTheTargetHasAnInvalidSize_ThenItIsNotReached()
    {
        // Arrange
        var search = new SoundPathSearch();
        search.Prepare(new GameArea(0, 0, 2, 2), new SoundMap());

        // Act
        var length = search.GetLength(new GameArea(int.MinValue, 0, int.MaxValue, 2));

        // Assert
        Assert.Equal(SoundPathSearch.NoPath, length);
    }

    [Fact]
    public void WhenNothingBlocks_ThenTheLengthIsTheOpenLength()
    {
        // Arrange
        var search = new SoundPathSearch();
        var source = new GameArea(0, 0, 2, 2);
        var target = new GameArea(13, 5, 2, 2);
        search.Prepare(source, new SoundMap());

        // Act
        var length = search.GetLength(target);

        // Assert
        Assert.Equal(SoundPathSearch.GetOpenLength(source, target), length);
    }

    [Fact]
    public void WhenNoSourceIsPrepared_ThenGettingALengthThrows()
    {
        // Arrange
        var search = new SoundPathSearch();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => search.GetLength(new GameArea(0, 0, 2, 2)));
    }

    [Fact]
    public void WhenTheSourceIsWiderThanTheLongestPath_ThenATargetBesideItsCenterGetsTheOpenLength()
    {
        // Arrange
        // The tiles at the ends of the source lie farther from its center than any path may run.
        var search = new SoundPathSearch();
        var source = new GameArea(0, 0, 200, 2);
        var target = new GameArea(100, 6, 2, 2);
        search.Prepare(source, new SoundMap());

        // Act
        var length = search.GetLength(target);

        // Assert
        Assert.Equal(SoundPathSearch.GetOpenLength(source, target), length);
    }

    [Fact]
    public void WhenASourceIsPreparedAgainWhileTheVersionStays_ThenItsLengthsComeFromTheCache()
    {
        // Arrange
        var index = new GameWorldIndex();
        index.AddOrUpdate(EntityFactory.Object(new Wall { X = -10, Y = 5, Width = 20, Depth = 1 }));
        var map = SoundMapFactory.Create(index);
        var search = new SoundPathSearch();
        var source = new GameArea(0, 0, 2, 2);
        var target = new GameArea(0, 10, 2, 2);
        search.Prepare(source, map);
        var length = search.GetLength(target);

        // Act
        search.Prepare(new GameArea(20, 0, 2, 2), map);
        search.Prepare(source, map);
        var cachedLength = search.GetLength(target);

        // Assert
        Assert.Equal(2, search.SearchCount);
        Assert.Equal(length, cachedLength);
        Assert.True(length > SoundPathSearch.GetOpenLength(source, target));
    }

    [Fact]
    public void WhenTheSoundVersionChanges_ThenTheSourceIsSearchedAgain()
    {
        // Arrange
        var index = new GameWorldIndex();
        var door = EntityFactory.Object(new Door { X = -10, Y = 5, Width = 20, Depth = 1, IsOpen = true });
        index.AddOrUpdate(door);
        var snapshot = SoundSnapshot.Empty.With(index);
        var map = new SoundMap();
        map.Update(snapshot);
        var search = new SoundPathSearch();
        var source = new GameArea(0, 0, 2, 2);
        var target = new GameArea(0, 10, 2, 2);
        search.Prepare(source, map);
        var openLength = search.GetLength(target);

        // Act
        ((Door)door.Subject).IsOpen = false;
        door.ReadFromSubject();
        index.AddOrUpdate(door);
        map.Update(snapshot.With(index));
        search.Prepare(source, map);
        var closedLength = search.GetLength(target);

        // Assert
        Assert.Equal(2, search.SearchCount);
        Assert.Equal(SoundPathSearch.GetOpenLength(source, target), openLength);
        Assert.True(closedLength > openLength);
    }

    [Fact]
    public void WhenMoreSourcesArePreparedThanTheCacheHolds_ThenTheRecentOnesStayCached()
    {
        // Arrange
        var map = new SoundMap();
        var search = new SoundPathSearch();
        for (var x = 0; x < 300; x++)
        {
            search.Prepare(new GameArea(x * 3, 0, 2, 2), map);
        }

        var searchCount = search.SearchCount;

        // Act
        search.Prepare(new GameArea(299 * 3, 0, 2, 2), map);
        search.Prepare(new GameArea(298 * 3, 0, 2, 2), map);
        var recentSearchCount = search.SearchCount;
        search.Prepare(new GameArea(0, 0, 2, 2), map);

        // Assert
        Assert.Equal(300, searchCount);
        Assert.Equal(300, recentSearchCount);
        Assert.Equal(301, search.SearchCount);
    }
}
