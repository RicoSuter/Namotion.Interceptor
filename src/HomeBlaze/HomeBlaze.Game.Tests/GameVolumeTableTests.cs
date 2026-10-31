using HomeBlaze.Game.Tests.Testing;
using HomeBlaze.Game.Voice;

namespace HomeBlaze.Game.Tests;

public class GameVolumeTableTests
{
    private readonly GameTestContext _context = new();

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

    private void Step(GamePlayer player, GameDirection direction)
    {
        _context.TimeProvider.Advance(Game.StepInterval);
        _context.Game.TryMove(player, direction, turnOnly: false);
    }

    [Fact]
    public void WhenTwoPlayersStandWithinRange_ThenEachHearsTheOther()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        _context.AddConnectedPlayer("tab-2", x: 10);

        // Act
        var isRebuilt = _context.Game.UpdateVolumeTable();

        // Assert
        var table = _context.Game.VolumeTable;
        Assert.True(isRebuilt);
        Assert.Equal(0.4f, FindVolume(table, "tab-1", "tab-2"));
        Assert.Equal(0.4f, FindVolume(table, "tab-2", "tab-1"));
    }

    [Fact]
    public void WhenAPlayerIsOutOfRange_ThenBothRowsAreEmpty()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        _context.AddConnectedPlayer("tab-2", x: 100);

        // Act
        _context.Game.UpdateVolumeTable();

        // Assert
        var table = _context.Game.VolumeTable;
        Assert.Equal(0, table.GetRow("tab-1").Length);
        Assert.Equal(0, table.GetRow("tab-2").Length);
    }

    [Fact]
    public void WhenAPlayerHasASizeThatTheIndexLeavesOut_ThenItHearsNobodyAndNobodyHearsIt()
    {
        // Arrange
        // A loaded configuration skips the range validation, as this replacement assigned before it joins the game does.
        // Its id is the smaller one and its center 11.5 tiles away, so it would compute the pair as the listener.
        _context.AddConnectedPlayer("tab-1");
        _context.AddConnectedPlayer("tab-0", x: 10);
        var huge = new GamePlayer { X = -500, Width = GameObject.MaximumSize + 1 };
        _context.Game.Players = new Dictionary<string, GamePlayer>(_context.Game.Players) { ["tab-0"] = huge };
        _context.Game.ReconcilePlayers();

        // Act
        _context.Game.UpdateVolumeTable();

        // Assert
        var table = _context.Game.VolumeTable;
        Assert.True(huge.IsConnected);
        Assert.Equal(0, table.GetRow("tab-0").Length);
        Assert.Equal(0, table.GetRow("tab-1").Length);
    }

    [Fact]
    public void WhenAPlayerIsOffline_ThenItHearsNobodyAndNobodyHearsIt()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        _context.Clients.Set("kitchen", isNamed: true, isConnected: false);
        _context.Game.ReconcilePlayers();

        // Act
        _context.Game.UpdateVolumeTable();

        // Assert
        var table = _context.Game.VolumeTable;
        Assert.Equal(0, table.GetRow("kitchen").Length);
        Assert.Null(FindVolume(table, "tab-1", "kitchen"));
    }

    [Fact]
    public void WhenAPlayerGoesOffline_ThenItsRowAndEntriesLeaveTheTable()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        _context.AddConnectedPlayer("tab-2", x: 10);
        _context.Game.UpdateVolumeTable();
        var before = FindVolume(_context.Game.VolumeTable, "tab-1", "tab-2");
        _context.Clients.Set("tab-2", isNamed: false, isConnected: false);
        _context.Game.ReconcilePlayers();
        _context.TimeProvider.Advance(GameVoiceSettings.VolumeTableInterval);

        // Act
        var isRebuilt = _context.Game.UpdateVolumeTable();

        // Assert
        var table = _context.Game.VolumeTable;
        Assert.Equal(0.4f, before);
        Assert.True(isRebuilt);
        Assert.Equal(0, table.GetRow("tab-2").Length);
        Assert.Null(FindVolume(table, "tab-1", "tab-2"));
        Assert.Null(FindVolume(table, "tab-2", "tab-1"));
    }

    [Fact]
    public void WhenAPlayerStepsBehindAWall_ThenThePairGetsQuieter()
    {
        // Arrange
        // The wall ends just north of the line between the players, next to the second one.
        _context.AddConnectedPlayer("tab-1");
        var second = _context.AddConnectedPlayer("tab-2", x: 6);
        _context.AddWall(5, 2, width: 1, depth: 10);
        _context.Game.UpdateVolumeTable();
        var before = FindVolume(_context.Game.VolumeTable, "tab-1", "tab-2");

        // Act
        Step(second, GameDirection.North);
        Step(second, GameDirection.North);
        Step(second, GameDirection.North);
        var isRebuilt = _context.Game.UpdateVolumeTable();

        // Assert
        // The voice goes around the end of the wall, so it is quieter than the straight distance of 6.7 tiles gives.
        var table = _context.Game.VolumeTable;
        var after = FindVolume(table, "tab-1", "tab-2");
        Assert.Equal((float)(4.0 / 6), before);
        Assert.True(isRebuilt);
        Assert.Equal((6, 3), (second.X, second.Y));
        Assert.NotNull(after);
        Assert.InRange(after.Value, 0.01f, (float)(4 / Math.Sqrt(45)) * 0.9f);
        Assert.Equal(after, FindVolume(table, "tab-2", "tab-1"));
    }

    [Fact]
    public async Task WhenTheDoorOfARoomBetweenThemCloses_ThenThePairLeavesTheTable()
    {
        // Arrange
        // A room with the inside from -2, 8 to 3, 13 around the second player and a door from -1 to 2 in its south wall.
        _context.AddConnectedPlayer("tab-1");
        _context.AddConnectedPlayer("tab-2", y: 10);
        _context.AddWall(-3, 7, width: 2);
        _context.AddWall(3, 7, width: 2);
        _context.AddWall(-3, 14, width: 8);
        _context.AddWall(-3, 8, width: 1, depth: 6);
        _context.AddWall(4, 8, width: 1, depth: 6);
        var door = _context.AddDoor(-1, 7, isOpen: true);
        _context.Game.UpdateVolumeTable();
        var before = FindVolume(_context.Game.VolumeTable, "tab-1", "tab-2");

        // Act
        await door.CloseAsync(CancellationToken.None);
        _context.TimeProvider.Advance(GameVoiceSettings.VolumeTableInterval);
        _context.Game.UpdateVolumeTable();

        // Assert
        Assert.Equal(0.4f, before);
        Assert.Null(FindVolume(_context.Game.VolumeTable, "tab-1", "tab-2"));
        Assert.Null(FindVolume(_context.Game.VolumeTable, "tab-2", "tab-1"));
    }

    [Fact]
    public async Task WhenADoorWithoutWallsBesideItCloses_ThenTheVoiceGoesAroundItAndGetsQuieter()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        _context.AddConnectedPlayer("tab-2", y: 10);
        var door = _context.AddDoor(-1, 5, isOpen: true);
        _context.Game.UpdateVolumeTable();
        var before = FindVolume(_context.Game.VolumeTable, "tab-1", "tab-2");

        // Act
        await door.CloseAsync(CancellationToken.None);
        _context.TimeProvider.Advance(GameVoiceSettings.VolumeTableInterval);
        _context.Game.UpdateVolumeTable();

        // Assert
        var after = FindVolume(_context.Game.VolumeTable, "tab-1", "tab-2");
        Assert.Equal(0.4f, before);
        Assert.NotNull(after);
        Assert.InRange(after.Value, 0.01f, 0.4f * 0.9f);
    }

    [Fact]
    public void WhenTheTableWasJustRebuilt_ThenAChangeWaitsForTheInterval()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        var second = _context.AddConnectedPlayer("tab-2", x: 10);
        _context.Game.UpdateVolumeTable();
        second.X = 12;
        _context.Game.RequestIndexRebuild();

        // Act
        var isRebuiltAtOnce = _context.Game.UpdateVolumeTable();
        _context.TimeProvider.Advance(GameVoiceSettings.VolumeTableInterval);
        var isRebuiltAfterInterval = _context.Game.UpdateVolumeTable();

        // Assert
        Assert.False(isRebuiltAtOnce);
        Assert.True(isRebuiltAfterInterval);
        Assert.Equal((float)(4.0 / 12), FindVolume(_context.Game.VolumeTable, "tab-1", "tab-2"));
    }

    [Fact]
    public void WhenNothingChanged_ThenTheTableIsNotRebuilt()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        _context.Game.UpdateVolumeTable();
        var table = _context.Game.VolumeTable;
        _context.TimeProvider.Advance(GameVoiceSettings.VolumeTableInterval);

        // Act
        var isRebuilt = _context.Game.UpdateVolumeTable();

        // Assert
        Assert.False(isRebuilt);
        Assert.Same(table, _context.Game.VolumeTable);
    }

    [Fact]
    public void WhenTheTableIsRebuilt_ThenVolumeTableChangedIsRaised()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        var raised = 0;
        _context.Game.VolumeTableChanged += () => raised++;

        // Act
        _context.Game.UpdateVolumeTable();
        _context.Game.UpdateVolumeTable();

        // Assert
        Assert.Equal(1, raised);
    }
}
