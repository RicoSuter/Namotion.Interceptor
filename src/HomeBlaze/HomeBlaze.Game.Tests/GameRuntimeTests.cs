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

    [Fact]
    public async Task WhenAllVoiceSwitchesAreOff_ThenTheVoiceLoopPausesUntilOneIsTurnedOn()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        await _context.Game.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => _context.Game.IsVoiceLoopPaused, "The voice loop did not pause", GameVoiceSettings.FrameDuration);

            // Act
            var turnOn = _context.Game.SetAudioAsync(player.ClientId, pageId: 1, isOn: true);
            await WaitUntilAsync(() => turnOn.IsCompleted && !_context.Game.IsVoiceLoopPaused, "The voice loop did not resume",
                GameVoiceSettings.FrameDuration);
            var turnOff = _context.Game.SetAudioAsync(player.ClientId, pageId: 1, isOn: false);
            await WaitUntilAsync(() => turnOff.IsCompleted && _context.Game.IsVoiceLoopPaused, "The voice loop did not pause again",
                GameVoiceSettings.FrameDuration);

            // Assert
            Assert.True(await turnOn);
            Assert.True(await turnOff);
        }
        finally
        {
            await _context.Game.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenTheMicrophoneIsTurnedOnWhileTheVoiceLoopPauses_ThenTheLoopMixesAgain()
    {
        // Arrange
        var talker = _context.AddConnectedPlayer("tab-1");
        _context.AddConnectedPlayer("tab-2", x: 20);
        var sink = new RecordingVoiceSink();
        using var registration = _context.Game.VoiceMixer.RegisterSink("tab-2", sink);
        await _context.Game.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => _context.Game.IsVoiceLoopPaused, "The voice loop did not pause", GameVoiceSettings.FrameDuration);

            // Act
            var turnOn = _context.Game.SetMicrophoneAsync(talker.ClientId, pageId: 1, isOn: true);
            await WaitUntilAsync(() => turnOn.IsCompleted, "The microphone was not turned on", GameVoiceSettings.FrameDuration);
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

    [Fact]
    public async Task WhenTheGameStartsAgainAfterAStop_ThenOutsideWritesAndStepsWork()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        var door = _context.AddDoor(-1, 2);
        var wall = _context.AddWall(20, 0);
        await _context.Game.StartAsync(CancellationToken.None);
        await _context.Game.StopAsync(CancellationToken.None);

        // A stopped game drops the write, so the next start takes the objects over again.
        door.IsOpen = true;

        // Act
        await _context.Game.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => _context.Game.IsFree(door.Area), "The door opened while stopped is still closed");
            wall.X = 30;
            await WaitUntilAsync(() => _context.Game.IsFree(new GameArea(20, 0, 4, 1)), "The wall moved after the restart is still there");
            _context.TimeProvider.Advance(Game.StepInterval);
            var step = _context.Game.StepAsync("tab-1", GameDirection.North, turnOnly: false);
            await WaitUntilAsync(() => step.IsCompleted, "The step after the restart did not run");

            // Assert
            Assert.True((await step)!.Value.IsAccepted);
        }
        finally
        {
            await _context.Game.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenTheGameStartsAgainBeforeATimedOutStopFinished_ThenTheNewRunWaitsForTheFormerOne()
    {
        // Arrange
        var door = _context.AddDoor(-1, 2);
        _context.AddConnectedPlayer("tab-1");
        using var finalTickGate = new ManualResetEventSlim();
        using var isHeld = new ManualResetEventSlim();
        var isArmed = false;

        // Holds the former run in its final tick, in which the queued door write makes the view session deliver.
        var sink = new RecordingViewSink();
        using var session = _context.OpenView("tab-1", sink);
        sink.Delivered = () =>
        {
            if (isArmed)
            {
                isArmed = false;
                isHeld.Set();
                finalTickGate.Wait();
            }
        };
        await _context.Game.StartAsync(CancellationToken.None);
        isArmed = true;
        door.IsOpen = true;
        // On another thread, because the cancellation may run the final tick inline, which the gate then holds.
        var stop = Task.Run(() => _context.Game.StopAsync(new CancellationToken(canceled: true)));
        await AsyncTestHelpers.WaitUntilAsync(() => isHeld.IsSet, message: "The former run did not reach its final tick");

        // Act
        await _context.Game.StartAsync(CancellationToken.None);
        try
        {
            finalTickGate.Set();
            await stop;
            Task<MoveResult?>? step = null;
            await WaitUntilAsync(() =>
            {
                if (step is null || step.IsFaulted)
                {
                    step = _context.Game.StepAsync("tab-1", GameDirection.North, turnOnly: false);
                }

                return step.IsCompletedSuccessfully;
            }, "The game did not run again after the former run finished");

            // Assert
            Assert.True((await step!)!.Value.IsAccepted);
        }
        finally
        {
            await _context.Game.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenAViewOpensWhileTheGameIsStopped_ThenItsFirstUpdateArrivesAfterTheRestart()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        _context.AddWall(-20, 8);
        await _context.Game.StartAsync(CancellationToken.None);
        await _context.Game.StopAsync(CancellationToken.None);
        var sink = new RecordingViewSink();

        // Act
        using var session = _context.Game.Views.Open("tab-1", sink);
        var deliveredWhileStopped = sink.Deliveries.Count;
        await _context.Game.StartAsync(CancellationToken.None);
        try
        {
            // Assert
            await WaitUntilAsync(() => sink.Deliveries.Count > 0, "The view got no update after the restart");
            Assert.Equal(0, deliveredWhileStopped);
            var update = sink.Deliveries[0].Update;
            Assert.Contains(update.Upserted, view => view.IsSelf);
            Assert.Contains(update.Upserted, view => view is { IsSelf: false, Payload.X: -20, Payload.Y: 8 });
        }
        finally
        {
            await _context.Game.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenTheGameStartsAgainWithAnObjectAddedWhileStopped_ThenTheOpenViewShowsIt()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        var sink = new RecordingViewSink();
        using var session = _context.OpenView("tab-1", sink);
        await _context.Game.StartAsync(CancellationToken.None);
        await _context.Game.StopAsync(CancellationToken.None);
        _context.Game.SetObjects(new Dictionary<string, GameObject>(_context.Game.GetObjects()) { ["wall-new"] = new Wall { X = -20, Y = 8 } });

        // Act
        await _context.Game.StartAsync(CancellationToken.None);
        try
        {
            // Assert
            await WaitUntilAsync(
                () => sink.Updates.Any(update => update.Upserted.Any(view => view is { IsSelf: false, Payload.X: -20, Payload.Y: 8 })),
                "The view did not show the object added while the game was stopped");
        }
        finally
        {
            await _context.Game.StopAsync(CancellationToken.None);
        }
    }
}

