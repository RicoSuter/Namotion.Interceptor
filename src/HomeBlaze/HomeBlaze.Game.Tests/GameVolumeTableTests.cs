using HomeBlaze.Game.Tests.Testing;
using HomeBlaze.Game.Voice;

namespace HomeBlaze.Game.Tests;

public class GameVolumeTableTests
{
    private readonly GameTestContext _context = new();

    private static float? FindVolume(GameVolumeTable table, string listenerId, string speakerId)
    {
        foreach (var entry in table.GetRow(listenerId))
        {
            if (entry.PlayerId == speakerId)
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
        Assert.Equal(0.75f, FindVolume(table, "tab-1", "tab-2"));
        Assert.Equal(0.75f, FindVolume(table, "tab-2", "tab-1"));
    }

    [Fact]
    public void WhenAPlayerIsOutOfRange_ThenBothRowsExistAndAreEmpty()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        _context.AddConnectedPlayer("tab-2", x: 100);

        // Act
        _context.Game.UpdateVolumeTable();

        // Assert
        var table = _context.Game.VolumeTable;
        Assert.True(table.HasRow("tab-1"));
        Assert.True(table.HasRow("tab-2"));
        Assert.Equal(0, table.GetRow("tab-1").Length);
        Assert.Equal(0, table.GetRow("tab-2").Length);
    }

    [Fact]
    public void WhenAPlayerIsOffline_ThenItHasNoRow()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        _context.Clients.Set("kitchen", isNamed: true, isConnected: false);
        _context.Game.ReconcilePlayers();

        // Act
        _context.Game.UpdateVolumeTable();

        // Assert
        var table = _context.Game.VolumeTable;
        Assert.False(table.HasRow("kitchen"));
        Assert.Null(FindVolume(table, "tab-1", "kitchen"));
    }

    [Fact]
    public void WhenAPlayerStepsBehindAWall_ThenThePairLeavesTheTable()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        var second = _context.AddConnectedPlayer("tab-2", x: 10);
        _context.AddWall(5, 2, width: 1, depth: 4);
        _context.Game.UpdateVolumeTable();
        var before = FindVolume(_context.Game.VolumeTable, "tab-1", "tab-2");

        // Act
        Step(second, GameDirection.North);
        Step(second, GameDirection.North);
        Step(second, GameDirection.North);
        var isRebuilt = _context.Game.UpdateVolumeTable();

        // Assert
        Assert.NotNull(before);
        Assert.True(isRebuilt);
        Assert.Equal((10, 3), (second.X, second.Y));
        Assert.Null(FindVolume(_context.Game.VolumeTable, "tab-1", "tab-2"));
        Assert.Null(FindVolume(_context.Game.VolumeTable, "tab-2", "tab-1"));
    }

    [Fact]
    public async Task WhenADoorBetweenThemCloses_ThenThePairLeavesTheTable()
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
        Assert.Equal(0.75f, before);
        Assert.Null(FindVolume(_context.Game.VolumeTable, "tab-1", "tab-2"));
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
        Assert.Equal(0.7f, FindVolume(_context.Game.VolumeTable, "tab-1", "tab-2"));
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
