using HomeBlaze.Game.Simulation;
using HomeBlaze.Game.Tests.Testing;
using HomeBlaze.Game.World;

namespace HomeBlaze.Game.Tests.World;

public class SoundMapTests
{
    private readonly GameWorldIndex _index = new();
    private readonly SoundMap _map = new();
    private SoundSnapshot _snapshot = SoundSnapshot.Empty;

    // A room with the inside from -2, 8 to 3, 13 and a door from -1 to 2 in its south wall.
    private GameEntity AddRoom(bool isDoorOpen)
    {
        AddWall(-3, 7, 2, 1);
        AddWall(3, 7, 2, 1);
        AddWall(-3, 14, 8, 1);
        AddWall(-3, 8, 1, 6);
        AddWall(4, 8, 1, 6);
        var door = EntityFactory.Object(new Door { X = -1, Y = 7, Width = 4, Depth = 1, IsOpen = isDoorOpen });
        _index.AddOrUpdate(door);
        return door;
    }

    private void AddWall(int x, int y, int width, int depth) =>
        _index.AddOrUpdate(EntityFactory.Object(new Wall { X = x, Y = y, Width = width, Depth = depth }));

    private void UpdateMap()
    {
        _snapshot = _snapshot.TakeChanges(_index);
        _map.Update(_snapshot);
    }

    private static GameArea Footprint(int x, int y) => new(x, y, GamePlayer.Size, GamePlayer.Size);

    [Fact]
    public void WhenOnePlayerIsInAClosedRoomAndOneOutside_ThenTheirAreasAreSeparated()
    {
        // Arrange
        AddRoom(isDoorOpen: false);
        UpdateMap();

        // Act
        var inside = _map.GetRegion(Footprint(0, 10));
        var outside = _map.GetRegion(Footprint(8, 0));

        // Assert
        Assert.True(inside > SoundMap.OpenRegion);
        Assert.Equal(SoundMap.OpenRegion, outside);
        Assert.True(SoundMap.AreSeparated(inside, outside));
    }

    [Fact]
    public void WhenTwoPlayersAreInTheSameClosedRoom_ThenTheyShareItsArea()
    {
        // Arrange
        AddRoom(isDoorOpen: false);
        UpdateMap();

        // Act
        var first = _map.GetRegion(Footprint(-2, 8));
        var second = _map.GetRegion(Footprint(2, 12));

        // Assert
        Assert.True(first > SoundMap.OpenRegion);
        Assert.Equal(first, second);
        Assert.False(SoundMap.AreSeparated(first, second));
    }

    [Fact]
    public void WhenTwoPlayersAreInDifferentClosedRooms_ThenTheirAreasAreSeparated()
    {
        // Arrange
        AddRoom(isDoorOpen: false);
        AddWall(10, 0, 6, 1);
        AddWall(10, 5, 6, 1);
        AddWall(10, 1, 1, 4);
        AddWall(15, 1, 1, 4);
        UpdateMap();

        // Act
        var first = _map.GetRegion(Footprint(0, 10));
        var second = _map.GetRegion(Footprint(12, 2));

        // Assert
        Assert.True(first > SoundMap.OpenRegion);
        Assert.True(second > SoundMap.OpenRegion);
        Assert.True(SoundMap.AreSeparated(first, second));
    }

    [Fact]
    public void WhenTheDoorOfTheRoomIsOpen_ThenInsideAndOutsideAreOneOpenArea()
    {
        // Arrange
        AddRoom(isDoorOpen: true);
        UpdateMap();

        // Act
        var inside = _map.GetRegion(Footprint(0, 10));
        var outside = _map.GetRegion(Footprint(8, 0));

        // Assert
        Assert.Equal(SoundMap.OpenRegion, inside);
        Assert.Equal(SoundMap.OpenRegion, outside);
        Assert.False(SoundMap.AreSeparated(inside, outside));
    }

    [Fact]
    public void WhenTheDoorCloses_ThenTheNewVersionLabelsTheRoomClosed()
    {
        // Arrange
        var door = AddRoom(isDoorOpen: true);
        UpdateMap();
        var openArea = _map.GetRegion(Footprint(0, 10));
        var openVersion = _map.Version;

        // Act
        ((Door)door.Subject).IsOpen = false;
        door.ReadFromSubject();
        _index.AddOrUpdate(door);
        UpdateMap();
        var closedArea = _map.GetRegion(Footprint(0, 10));

        // Assert
        Assert.Equal(SoundMap.OpenRegion, openArea);
        Assert.True(closedArea > SoundMap.OpenRegion);
        Assert.True(_map.Version > openVersion);
    }

    [Fact]
    public void WhenAFootprintOverlapsAWall_ThenItsAreaIsUnknownAndSeparatesFromNothing()
    {
        // Arrange
        AddRoom(isDoorOpen: false);
        UpdateMap();

        // Act
        var inWall = _map.GetRegion(Footprint(-4, 10));
        var inside = _map.GetRegion(Footprint(0, 10));

        // Assert
        Assert.Equal(SoundMap.UnknownRegion, inWall);
        Assert.False(SoundMap.AreSeparated(inWall, inside));
    }

    [Fact]
    public void WhenAnAreaIsClosedButLargerThanTheMaximum_ThenItCountsAsOpen()
    {
        // Arrange
        // A closed square whose inside holds more tiles than a closed region may have.
        var side = (int)Math.Sqrt(SoundMap.MaximumClosedTiles) + 2;
        AddWall(-1, -1, side + 2, 1);
        AddWall(-1, side, side + 2, 1);
        AddWall(-1, 0, 1, side);
        AddWall(side, 0, 1, side);
        UpdateMap();

        // Act
        var area = _map.GetRegion(Footprint(0, 0));

        // Assert
        Assert.Equal(SoundMap.OpenRegion, area);
    }

    [Fact]
    public void WhenTwoFootprintsAreSeparated_ThenTheSearchFindsNoPathBetweenThem()
    {
        // Arrange
        // Random walls of random lengths, with gaps, around closed and open pockets.
        var random = new Random(4711);
        for (var i = 0; i < 120; i++)
        {
            var isHorizontal = random.Next(2) == 0;
            var length = random.Next(1, 16);
            AddWall(random.Next(-30, 30), random.Next(-30, 30), isHorizontal ? length : 1, isHorizontal ? 1 : length);
        }

        UpdateMap();
        var search = new SoundPathSearch();
        var footprints = new List<GameArea>();
        for (var y = -30; y < 30; y += 3)
        {
            for (var x = -30; x < 30; x += 3)
            {
                footprints.Add(Footprint(x, y));
            }
        }

        // Act
        var separatedCount = 0;
        var reachedSeparatedCount = 0;
        foreach (var source in footprints)
        {
            search.Prepare(source, _map);
            var (separated, reached) = CountSeparated(source, footprints, search);
            separatedCount += separated;
            reachedSeparatedCount += reached;
        }

        // Assert
        Assert.True(separatedCount > 0);
        Assert.Equal(0, reachedSeparatedCount);
    }

    // Counts the targets separated from the prepared source, and those of them that the search reaches.
    private (int Separated, int Reached) CountSeparated(GameArea source, List<GameArea> targets, SoundPathSearch search)
    {
        var separated = 0;
        var reached = 0;
        foreach (var target in targets)
        {
            if (SoundMap.AreSeparated(_map.GetRegion(source), _map.GetRegion(target)))
            {
                separated++;
                reached += search.GetLength(target) != SoundPathSearch.NoPath ? 1 : 0;
            }
        }

        return (separated, reached);
    }
}
