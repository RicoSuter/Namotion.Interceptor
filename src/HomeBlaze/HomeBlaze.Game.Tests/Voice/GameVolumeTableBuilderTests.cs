using HomeBlaze.Game.Simulation;
using HomeBlaze.Game.Tests.Testing;
using HomeBlaze.Game.Voice;
using HomeBlaze.Game.World;

namespace HomeBlaze.Game.Tests.Voice;

public class GameVolumeTableBuilderTests
{
    private readonly GameWorldIndex _index = new();
    private readonly GameVolumeTableBuilder _builder = new();
    private SoundSnapshot _snapshot = SoundSnapshot.Empty;

    private GameVolumeTable Build(params HearingPosition[] players)
    {
        _snapshot = _snapshot.With(_index);
        return _builder.Build(players, _snapshot);
    }

    private void AddWall(int x, int y, int width, int depth) =>
        _index.AddOrUpdate(EntityFactory.Object(new Wall { X = x, Y = y, Width = width, Depth = depth }));

    private static HearingPosition Player(string clientId, int x, int y) => new(clientId, new GameArea(x, y, GamePlayer.Size, GamePlayer.Size));

    private static float? FindVolume(GameVolumeTable table, string listenerId, string talkerId)
    {
        foreach (var entry in table.GetRow(listenerId))
        {
            if (entry.PlayerId == talkerId)
            {
                return entry.Volume;
            }
        }

        return null;
    }

    [Fact]
    public void WhenOnlyATalkerMoves_ThenTheListenersPathsAreReused()
    {
        // Arrange
        // The wall stands between the listener and the talker, so the pair needs a search from the listener.
        AddWall(-10, 5, 20, 1);
        var first = Build(Player("a", 0, 0), Player("b", 0, 10));
        var searchCount = _builder.SearchForTest.SearchCount;

        // Act
        var second = Build(Player("a", 0, 0), Player("b", 1, 10));

        // Assert
        Assert.Equal(1, searchCount);
        Assert.Equal(1, _builder.SearchForTest.SearchCount);
        Assert.NotNull(FindVolume(first, "a", "b"));
        Assert.NotNull(FindVolume(second, "a", "b"));
        Assert.Equal(FindVolume(second, "a", "b"), FindVolume(second, "b", "a"));
    }

    [Fact]
    public void WhenAPlayerIsInAClosedRoom_ThenItsPairsWithPlayersOutsideNeedNoSearch()
    {
        // Arrange
        AddWall(-3, 7, 8, 1);
        AddWall(-3, 14, 8, 1);
        AddWall(-3, 8, 1, 6);
        AddWall(4, 8, 1, 6);

        // Act
        var table = Build(Player("a", 0, 10), Player("b", 8, 0), Player("c", 0, 0));

        // Assert
        Assert.Equal(0, _builder.SearchForTest.SearchCount);
        Assert.Equal(0, table.GetRow("a").Length);
        Assert.NotNull(FindVolume(table, "b", "c"));
    }

    [Fact]
    public void WhenTheTableIsBuilt_ThenEveryPairHasTheVolumeOfTheHearingRange()
    {
        // Arrange
        AddWall(-10, 5, 20, 1);
        AddWall(6, -8, 1, 10);
        HearingPosition[] players = [Player("a", 0, 0), Player("b", 0, 10), Player("c", 12, -3), Player("d", -20, 2), Player("e", 30, 30)];

        // Act
        var table = Build(players);

        // Assert
        var map = new SoundMap();
        map.Update(_snapshot);
        foreach (var listener in players)
        {
            foreach (var talker in players)
            {
                if (listener.ClientId == talker.ClientId)
                {
                    continue;
                }

                var volume = (float)HearingRange.GetVolume(listener, talker, map, new SoundPathSearch());
                Assert.Equal(volume > 0 ? volume : null, FindVolume(table, listener.ClientId, talker.ClientId));
            }
        }
    }
}
