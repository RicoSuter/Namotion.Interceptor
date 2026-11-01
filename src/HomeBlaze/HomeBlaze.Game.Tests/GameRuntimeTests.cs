using HomeBlaze.Game.Tests.Testing;
using HomeBlaze.Game.Voice;
using Namotion.Interceptor.Testing;

namespace HomeBlaze.Game.Tests;

public class GameRuntimeTests
{
    private readonly GameTestContext _context = new();

    // Once the game runs, its own loop ticks on the clock, so the test only advances the clock and never ticks itself.
    private Task WaitUntilAsync(Func<bool> condition, string message, TimeSpan? step = null) =>
        AsyncTestHelpers.WaitUntilAsync(() =>
        {
            _context.TimeProvider.Advance(step ?? GameSimulationSettings.TickInterval);
            return condition();
        }, message: message);

    [Fact]
    public async Task WhenTheGameIsSignaled_ThenItReconcilesItsPlayers()
    {
        // Arrange
        await _context.Game.StartAsync(CancellationToken.None);
        try
        {
            _context.Clients.Set("tab-1", isNamed: false, isConnected: true);

            // Act
            _context.Game.SignalReconcile();

            // Assert
            await WaitUntilAsync(() => _context.Game.Players.ContainsKey("tab-1"), "The player did not join");
        }
        finally
        {
            await _context.Game.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenTheHousekeepingIntervalPassedBeforeASignal_ThenTheSignalStillReconcilesPromptly()
    {
        // Arrange
        _context.Clients.Set("tab-1", isNamed: false, isConnected: true);
        await _context.Game.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => _context.Game.Players.ContainsKey("tab-1"), "The first pass did not run");

            // Only a pass that a won delay starts picks up tab-2; the clock advances until the loop waits on its delay.
            _context.Clients.Set("tab-2", isNamed: false, isConnected: true);
            await WaitUntilAsync(() => _context.Game.Players.ContainsKey("tab-2"), "The housekeeping pass did not run", Game.HousekeepingInterval);
            _context.Clients.Set("tab-3", isNamed: false, isConnected: true);

            // Act
            _context.Game.SignalReconcile();

            // Assert
            await WaitUntilAsync(() => _context.Game.Players.ContainsKey("tab-3"), "The signal did not wake the loop");
        }
        finally
        {
            await _context.Game.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenPlayersAreReplacedFromOutsideWhileRunning_ThenThePlayersAreReconciled()
    {
        // Arrange
        _context.Clients.Set("tab-1", isNamed: false, isConnected: true);
        await _context.Game.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => _context.Game.Players.ContainsKey("tab-1"), "The first pass did not run");
            _context.Clients.Set("kitchen", isNamed: true, isConnected: true);

            // Act
            _context.Game.Players = new Dictionary<string, GamePlayer>(_context.Game.Players) { ["kitchen"] = new GamePlayer { X = 40 } };

            // Assert
            await WaitUntilAsync(() => _context.Game.Players.TryGetValue("kitchen", out var kitchen) && kitchen.IsConnected,
                "The replaced players were not reconciled");
        }
        finally
        {
            await _context.Game.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenTheConfigurationIsApplied_ThenThePlayersAreReconciled()
    {
        // Arrange
        _context.Clients.Set("tab-1", isNamed: false, isConnected: true);
        await _context.Game.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => _context.Game.Players.ContainsKey("tab-1"), "The first pass did not run");
            _context.Clients.Set("tab-2", isNamed: false, isConnected: true);

            // Act
            await _context.Game.ApplyConfigurationAsync(CancellationToken.None);

            // Assert
            await WaitUntilAsync(() => _context.Game.Players.ContainsKey("tab-2"), "Applying the configuration did not reconcile the players");
        }
        finally
        {
            await _context.Game.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenAWallIsMovedFromOutside_ThenMovementSeesItsNewPosition()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var wall = _context.AddWall(10, 10);

        // Act
        wall.X = 2;
        wall.Y = 0;
        wall.Width = 1;
        var result = await _context.StepAsync(player.ClientId, GameDirection.East);

        // Assert
        Assert.False(result!.Value.IsAccepted);
    }

    [Fact]
    public void WhenAWallIsLoweredFromOutside_ThenItNoLongerBlocksSoundBetweenPlayers()
    {
        // Arrange
        // Too long for the voice to go around within the hearing range.
        _context.AddConnectedPlayer("tab-1");
        _context.AddConnectedPlayer("tab-2", y: 10);
        var wall = _context.AddObject("wall", new Wall { X = -100, Y = 5, Width = 200, Height = 10 });
        _context.Tick(2);
        var before = FindVolume("tab-1", "tab-2");

        // Act
        wall.Height = 4;
        _context.Tick(2);

        // Assert
        Assert.Null(before);
        Assert.Equal(0.4f, FindVolume("tab-1", "tab-2"));
        Assert.Equal(0.4f, FindVolume("tab-2", "tab-1"));
    }

    [Fact]
    public async Task WhenADoorIsOpenedFromOutside_ThenTheNextStepSeesItOpen()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var door = _context.AddDoor(-1, 2);

        // Act
        door.IsOpen = true;
        var result = await _context.StepAsync(player.ClientId, GameDirection.North);

        // Assert
        Assert.True(result!.Value.IsAccepted);
    }

    [Fact]
    public async Task WhenTheGameRuns_ThenTheVoiceLoopMixesEveryFrameDuration()
    {
        // Arrange
        var talker = _context.AddConnectedPlayer("tab-1");
        _context.AddConnectedPlayer("tab-2", x: 20);
        var sink = new RecordingVoiceSink();
        using var registration = _context.Game.VoiceMixer.RegisterSink("tab-2", sink);
        await _context.RunAsync(_context.Game.SetMicrophoneAsync(talker.ClientId, pageId: 1, isOn: true));
        _context.Tick(2);
        await _context.Game.StartAsync(CancellationToken.None);
        try
        {
            // Act
            await WaitUntilAsync(() =>
            {
                _context.Game.VoiceMixer.Receive("tab-1", 1, VoiceFrames.Create(1000));
                return sink.Count > 0;
            }, "The voice loop did not mix the frame", GameVoiceSettings.FrameDuration);

            // Assert
            Assert.Equal(200, VoiceFrames.ReadLastSample(sink.Frames[0]));
        }
        finally
        {
            await _context.Game.StopAsync(CancellationToken.None);
        }
    }

    private float? FindVolume(string listenerId, string talkerId)
    {
        foreach (var entry in _context.Game.VolumeTable.GetRow(listenerId))
        {
            if (entry.PlayerId == talkerId)
            {
                return entry.Volume;
            }
        }

        return null;
    }
}
