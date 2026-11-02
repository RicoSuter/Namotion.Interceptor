using HomeBlaze.Game.Simulation;
using HomeBlaze.Game.Tests.Testing;
using HomeBlaze.Game.World;

namespace HomeBlaze.Game.Tests.Simulation;

public class SoundSnapshotTests
{
    [Fact]
    public void WhenOnlyPlayersMove_ThenTheSnapshotStaysTheSameInstance()
    {
        // Arrange
        var index = new GameWorldIndex();
        index.AddOrUpdate(EntityFactory.Object(new Wall { X = 0, Y = 4, Width = 8, Depth = 1 }));
        var snapshot = SoundSnapshot.Empty.With(index);
        var player = EntityFactory.Player("tab-1", 0, 0);
        index.AddOrUpdate(player);
        player.Area = player.Area with { X = 40 };
        index.AddOrUpdate(player);

        // Act
        var next = snapshot.With(index);

        // Assert
        Assert.Same(snapshot, next);
        Assert.True(next.GetChunk((0, 0)).IsBlocked(7, 4));
    }

    [Fact]
    public void WhenADoorCloses_ThenANewSnapshotHasItsTilesAndVersionAndTheOldOneStaysUnchanged()
    {
        // Arrange
        var index = new GameWorldIndex();
        var door = EntityFactory.Object(new Door { X = 18, Y = 4, Width = 4, Depth = 1, IsOpen = true });
        index.AddOrUpdate(door);
        var open = SoundSnapshot.Empty.With(index);

        // Act
        ((Door)door.Subject).IsOpen = false;
        door.ReadFromSubject();
        index.AddOrUpdate(door);
        var closed = open.With(index);

        // Assert
        Assert.NotSame(open, closed);
        Assert.Equal(index.SoundVersion, closed.Version);
        Assert.True(closed.Version > open.Version);
        Assert.True(closed.GetChunk((1, 0)).IsBlocked(2, 4));
        Assert.True(closed.GetChunk((1, 0)).IsBlocked(5, 4));
        Assert.True(open.GetChunk((1, 0)).IsEmpty);
        Assert.Equal(0, index.SoundChanges.Count);
    }

    [Fact]
    public void WhenTheLastWallOfAChunkIsRemoved_ThenTheChunkIsEmpty()
    {
        // Arrange
        var index = new GameWorldIndex();
        var wall = EntityFactory.Object(new Wall { X = 0, Y = 4, Width = 8, Depth = 1 });
        index.AddOrUpdate(wall);
        var snapshot = SoundSnapshot.Empty.With(index);

        // Act
        index.Remove(wall);
        var next = snapshot.With(index);

        // Assert
        Assert.False(snapshot.GetChunk((0, 0)).IsEmpty);
        Assert.True(next.GetChunk((0, 0)).IsEmpty);
    }
}
