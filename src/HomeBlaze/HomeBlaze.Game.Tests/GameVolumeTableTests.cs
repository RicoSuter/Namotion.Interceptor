using Microsoft.Extensions.Logging;
using HomeBlaze.Game.Simulation;
using HomeBlaze.Game.Tests.Testing;
using HomeBlaze.Game.Voice;
using HomeBlaze.Game.World;

namespace HomeBlaze.Game.Tests;

public class GameVolumeTableTests
{
    private readonly GameTestContext _context = new();
    private int _rebuilds;

    public GameVolumeTableTests()
    {
        _context.Game.VolumeTableChanged += () => _rebuilds++;
    }

    // Runs the ticks until a change is in the table; returns whether the table was rebuilt meanwhile.
    private bool Rebuild()
    {
        var before = _rebuilds;
        _context.BuildVolumeTable();
        return _rebuilds > before;
    }

    // Runs one tick; returns whether the table was rebuilt in it.
    private bool TickOnce()
    {
        var before = _rebuilds;
        _context.Tick();
        return _rebuilds > before;
    }

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
    public void WhenTwoPlayersStandWithinRange_ThenEachHearsTheOther()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        _context.AddConnectedPlayer("tab-2", x: 10);

        // Act
        Rebuild();

        // Assert
        var table = _context.Game.VolumeTable;
        Assert.Equal(0.4f, FindVolume(table, "tab-1", "tab-2"));
        Assert.Equal(0.4f, FindVolume(table, "tab-2", "tab-1"));
    }

    [Fact]
    public void WhenATreeStandsBetweenTwoPlayers_ThenTheyHearEachOtherAsWithoutIt()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        _context.AddConnectedPlayer("tab-2", x: 10);
        _context.AddObject("tree", new Tree { X = 5, Y = 0, Variant = 6 });

        // Act
        Rebuild();

        // Assert
        var table = _context.Game.VolumeTable;
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
        Rebuild();

        // Assert
        var table = _context.Game.VolumeTable;
        Assert.Equal(0, table.GetRow("tab-1").Length);
        Assert.Equal(0, table.GetRow("tab-2").Length);
    }

    [Fact]
    public void WhenAPlayerHasASizeThatTheIndexLeavesOut_ThenItHearsNobodyAndNobodyHearsIt()
    {
        // Arrange
        // A loaded configuration skips the range validation. Its id is the smaller one and its center is near the other
        // player, so it would compute the pair as the listener.
        _context.AddConnectedPlayer("tab-1");
        _context.AddConnectedPlayer("tab-0", x: 10);
        var huge = _context.Game.WorldForTest.Players["tab-0"];
        huge.Area = huge.Area with { X = -500, Width = GameObject.MaximumSize + 1 };
        _context.Game.WorldForTest.Index.AddOrUpdate(huge);

        // Act
        Rebuild();

        // Assert
        var table = _context.Game.VolumeTable;
        Assert.Equal(0, table.GetRow("tab-0").Length);
        Assert.Equal(0, table.GetRow("tab-1").Length);
    }

    [Fact]
    public void WhenAPlayerIsOffline_ThenItHearsNobodyAndNobodyHearsIt()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        _context.Clients.Set("kitchen", isNamed: true, isConnected: false);
        _context.Reconcile();

        // Act
        Rebuild();

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
        Rebuild();
        var before = FindVolume(_context.Game.VolumeTable, "tab-1", "tab-2");
        _context.Clients.Set("tab-2", isNamed: false, isConnected: false);
        _context.Reconcile();

        // Act
        Rebuild();

        // Assert
        var table = _context.Game.VolumeTable;
        Assert.Equal(0.4f, before);
        Assert.Equal(0, table.GetRow("tab-2").Length);
        Assert.Null(FindVolume(table, "tab-1", "tab-2"));
        Assert.Null(FindVolume(table, "tab-2", "tab-1"));
    }

    [Fact]
    public async Task WhenAPlayerStepsBehindAWall_ThenThePairGetsQuieter()
    {
        // Arrange
        // The wall ends just north of the line between the players, next to the second one.
        _context.AddConnectedPlayer("tab-1");
        var second = _context.AddConnectedPlayer("tab-2", x: 6);
        _context.AddWall(5, 2, width: 1, depth: 10);
        Rebuild();
        var before = FindVolume(_context.Game.VolumeTable, "tab-1", "tab-2");

        // Act
        await _context.StepAsync("tab-2", GameDirection.North);
        await _context.StepAsync("tab-2", GameDirection.North);
        await _context.StepAsync("tab-2", GameDirection.North);
        Rebuild();
        _context.Publish();

        // Assert
        // The voice goes around the end of the wall, so it is quieter than the straight distance of 6.7 tiles gives.
        var table = _context.Game.VolumeTable;
        var after = FindVolume(table, "tab-1", "tab-2");
        Assert.Equal((float)(4.0 / 6), before);
        Assert.Equal((6, 3), (second.X, second.Y));
        Assert.NotNull(after);
        Assert.InRange(after.Value, 0.01f, (float)(4 / Math.Sqrt(45)) * 0.9f);
        Assert.Equal(after, FindVolume(table, "tab-2", "tab-1"));
    }

    [Fact]
    public void WhenTheDoorOfARoomBetweenThemCloses_ThenThePairLeavesTheTable()
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
        Rebuild();
        var before = FindVolume(_context.Game.VolumeTable, "tab-1", "tab-2");

        // Act
        door.Close();
        Rebuild();

        // Assert
        Assert.Equal(0.4f, before);
        Assert.Null(FindVolume(_context.Game.VolumeTable, "tab-1", "tab-2"));
        Assert.Null(FindVolume(_context.Game.VolumeTable, "tab-2", "tab-1"));
    }

    [Fact]
    public void WhenADoorWithoutWallsBesideItCloses_ThenTheVoiceGoesAroundItAndGetsQuieter()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        _context.AddConnectedPlayer("tab-2", y: 10);
        var door = _context.AddDoor(-1, 5, isOpen: true);
        Rebuild();
        var before = FindVolume(_context.Game.VolumeTable, "tab-1", "tab-2");

        // Act
        door.Close();
        Rebuild();

        // Assert
        var after = FindVolume(_context.Game.VolumeTable, "tab-1", "tab-2");
        Assert.Equal(0.4f, before);
        Assert.NotNull(after);
        Assert.InRange(after.Value, 0.01f, 0.4f * 0.9f);
    }

    [Theory]
    [InlineData(4, 0.4f)]
    [InlineData(8, null)]
    public void WhenAWallStandsBetweenTwoPlayers_ThenOnlyATallWallBlocksTheVoice(int height, float? volume)
    {
        // Arrange
        // Too long for the voice to go around within the hearing range.
        _context.AddConnectedPlayer("tab-1");
        _context.AddConnectedPlayer("tab-2", y: 10);
        _context.AddObject("wall", new Wall { X = -100, Y = 5, Width = 200, Height = height });

        // Act
        Rebuild();

        // Assert
        Assert.Equal(volume, FindVolume(_context.Game.VolumeTable, "tab-1", "tab-2"));
        Assert.Equal(volume, FindVolume(_context.Game.VolumeTable, "tab-2", "tab-1"));
    }

    [Fact]
    public void WhenALowWallBetweenTwoPlayersGrowsToTwoMeters_ThenThePairLeavesTheTable()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        _context.AddConnectedPlayer("tab-2", y: 10);
        var wall = _context.AddObject("wall", new Wall { X = -100, Y = 5, Width = 200, Height = 4 });
        Rebuild();
        var before = FindVolume(_context.Game.VolumeTable, "tab-1", "tab-2");

        // Act
        wall.Height = 8;
        var isRebuilt = Rebuild();

        // Assert
        Assert.Equal(0.4f, before);
        Assert.True(isRebuilt);
        Assert.Null(FindVolume(_context.Game.VolumeTable, "tab-1", "tab-2"));
    }

    [Fact]
    public void WhenTheTableWasJustRebuilt_ThenAChangeWaitsForTheInterval()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        var second = _context.AddConnectedPlayer("tab-2", x: 10);
        second.X = 11;
        var ticks = 0;
        while (!TickOnce() && ++ticks < 10)
        {
            // Until the move is in a table.
        }

        second.X = 12;

        // Act
        var isRebuiltAtOnce = TickOnce();
        var isRebuiltAfterInterval = TickOnce();

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
        Rebuild();
        var table = _context.Game.VolumeTable;

        // Act
        var isRebuilt = Rebuild();

        // Assert
        Assert.False(isRebuilt);
        Assert.Same(table, _context.Game.VolumeTable);
    }

    [Fact]
    public void WhenTheTableIsRebuilt_ThenVolumeTableChangedIsRaised()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        Rebuild();
        var raised = 0;
        _context.Game.VolumeTableChanged += () => raised++;
        _context.Clients.Set("tab-2", isNamed: false, isConnected: true);
        _context.Game.PostReconcile();

        // Act
        Rebuild();
        Rebuild();

        // Assert
        Assert.Equal(1, raised);
    }

    // Runs a tick without GameTestContext.Tick, which would wait for a held build.
    private void TickWithoutWaiting()
    {
        _context.TimeProvider.Advance(GameSimulationSettings.TickInterval);
        _context.Game.RunTickForTest();
    }

    [Fact]
    public async Task WhenABuildIsHeld_ThenTicksGoOnAndTheTableFollowsOnceItCompletes()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        var second = _context.AddConnectedPlayer("tab-2", x: 10);
        Rebuild();
        var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _context.Game.VolumeTableBuildBlockerForTest = blocker.Task;
        second.X = 20;

        // Act
        for (var i = 0; i < 5; i++)
        {
            TickWithoutWaiting();
        }

        var isBuildRunning = !_context.Game.VolumeTableBuildTask.IsCompleted;
        var volumeWhileHeld = FindVolume(_context.Game.VolumeTable, "tab-1", "tab-2");
        _context.TimeProvider.Advance(Game.StepInterval);
        var step = _context.Game.StepAsync("tab-1", GameDirection.West, turnOnly: false);
        _context.Game.RunTickForTest();
        var isStepCompletedWhileHeld = step.IsCompleted;
        blocker.SetResult();
        await _context.Game.VolumeTableBuildTask;
        TickWithoutWaiting();

        // Assert
        Assert.True(isBuildRunning);
        Assert.Equal(0.4f, volumeWhileHeld);
        Assert.True(isStepCompletedWhileHeld);
        Assert.Equal(0.2f, FindVolume(_context.Game.VolumeTable, "tab-1", "tab-2"));
    }

    [Fact]
    public async Task WhenTheGameStopsDuringABuild_ThenItsTableIsNeverPublishedAndARestartBuildsAfresh()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        var second = _context.AddConnectedPlayer("tab-2", x: 10);
        Rebuild();
        var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _context.Game.VolumeTableBuildBlockerForTest = blocker.Task;
        second.X = 20;
        for (var i = 0; i < 3; i++)
        {
            TickWithoutWaiting();
        }

        var heldBuild = _context.Game.VolumeTableBuildTask;
        var publishedVolumes = new List<float?>();
        _context.Game.VolumeTableChanged += () => publishedVolumes.Add(FindVolume(_context.Game.VolumeTable, "tab-1", "tab-2"));

        // Act
        var isHeldAtStop = !heldBuild.IsCompleted;
        await _context.Game.StopAsync(CancellationToken.None);
        blocker.SetResult();
        await heldBuild;
        var volumeAfterStop = FindVolume(_context.Game.VolumeTable, "tab-1", "tab-2");
        var publishedAfterStop = publishedVolumes.Count;

        // Written while stopped, so only the restart's take-over reads it.
        second.X = 16;
        _context.Game.VolumeTableBuildBlockerForTest = null;
        _context.Game.StartForTest();
        Rebuild();

        // Assert
        Assert.True(isHeldAtStop);
        Assert.False(heldBuild.IsFaulted);
        Assert.Equal(0.4f, volumeAfterStop);
        Assert.Equal(0, publishedAfterStop);
        Assert.Equal(0.25f, FindVolume(_context.Game.VolumeTable, "tab-1", "tab-2"));
        Assert.DoesNotContain(0.2f, publishedVolumes);
    }

    [Fact]
    public void WhenABuildFails_ThenItIsLoggedAndBuiltAgainAfterTheInterval()
    {
        // Arrange
        var logger = new RecordingLogger();
        var context = new GameTestContext(logger);
        context.AddConnectedPlayer("tab-1");
        var second = context.AddConnectedPlayer("tab-2", x: 10);
        context.BuildVolumeTable();
        context.Game.VolumeTableBuildBlockerForTest = Task.FromException(new InvalidOperationException("The build failed."));
        second.X = 20;

        // Act
        context.BuildVolumeTable();
        var volumeAfterFailure = FindVolume(context.Game.VolumeTable, "tab-1", "tab-2");
        context.BuildVolumeTable();
        var warningsWhileFailing = logger.Entries.Count(entry => entry.Level == LogLevel.Warning);
        context.Game.VolumeTableBuildBlockerForTest = null;
        context.BuildVolumeTable();

        // Assert
        Assert.Equal(0.4f, volumeAfterFailure);
        Assert.Equal(1, warningsWhileFailing);
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("volume table"));
        Assert.Equal(0.2f, FindVolume(context.Game.VolumeTable, "tab-1", "tab-2"));
    }

    [Fact]
    public void WhenOnlyAnObjectThatLetsSoundThroughMoves_ThenTheTableIsNotRebuilt()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        _context.AddConnectedPlayer("tab-2", x: 10);
        var tree = _context.AddObject("tree", new Tree { X = 5, Y = 20, Variant = 6 });
        Rebuild();

        // Act
        tree.X = 6;
        var isRebuilt = Rebuild();

        // Assert
        Assert.False(isRebuilt);
    }
}
