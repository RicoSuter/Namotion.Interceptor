using HomeBlaze.Game.Tests.Testing;
using HomeBlaze.Game.Voice;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Testing;

namespace HomeBlaze.Game.Tests;

public class GameRuntimeTests
{
    private readonly GameTestContext _context = new();

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
            await AsyncTestHelpers.WaitUntilAsync(() => _context.Game.Players.ContainsKey("tab-1"), message: "The player did not join");
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
            await AsyncTestHelpers.WaitUntilAsync(() => _context.Game.Players.ContainsKey("tab-1"), message: "The first pass did not run");

            // Only a pass that a won delay starts picks up tab-2; the clock advances until the loop waits on its delay.
            _context.Clients.Set("tab-2", isNamed: false, isConnected: true);
            await AsyncTestHelpers.WaitUntilAsync(() =>
            {
                _context.TimeProvider.Advance(Game.HousekeepingInterval);
                return _context.Game.Players.ContainsKey("tab-2");
            }, message: "The housekeeping pass did not run");
            _context.Clients.Set("tab-3", isNamed: false, isConnected: true);

            // Act
            _context.Game.SignalReconcile();

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => _context.Game.Players.ContainsKey("tab-3"), timeout: TimeSpan.FromSeconds(2),
                message: "The signal did not wake the loop");
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
            await AsyncTestHelpers.WaitUntilAsync(() => _context.Game.Players.ContainsKey("tab-1"), message: "The first pass did not run");
            _context.Clients.Set("kitchen", isNamed: true, isConnected: true);

            // Act
            _context.Game.Players = new Dictionary<string, GamePlayer>(_context.Game.Players) { ["kitchen"] = new GamePlayer { X = 40 } };

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => _context.Game.Players.TryGetValue("kitchen", out var kitchen) && kitchen.IsConnected,
                timeout: TimeSpan.FromSeconds(2), message: "The replaced players were not reconciled");
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
            await AsyncTestHelpers.WaitUntilAsync(() => _context.Game.Players.ContainsKey("tab-1"), message: "The first pass did not run");
            _context.Clients.Set("tab-2", isNamed: false, isConnected: true);

            // Act
            await _context.Game.ApplyConfigurationAsync(CancellationToken.None);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => _context.Game.Players.ContainsKey("tab-2"), timeout: TimeSpan.FromSeconds(2),
                message: "Applying the configuration did not reconcile the players");
        }
        finally
        {
            await _context.Game.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenAnObjectIsMovedWhileTheGameHoldsItsLock_ThenTheMoveDoesNotWaitForTheGame()
    {
        // Arrange
        var timeProvider = new FrozenTimeProvider();
        var clients = new FakeClientDirectory();
        var root = new TestRoot(SubjectContextFactory.Create(new ServiceCollection()));
        var game = new Game(clients, timeProvider, NullLogger.Instance);
        root.Game = game;
        var wall = new Wall { X = 10, Y = 10 };
        game.Objects = new Dictionary<string, GameObject> { ["wall"] = wall };
        clients.Set("tab-1", isNamed: false, isConnected: true);
        await game.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(() => game.Players.TryGetValue("tab-1", out var joined) && joined.IsConnected,
                message: "The player did not join");
            var player = game.Players["tab-1"];

            // The clock stands still, so these steps use up the burst and the armed step is refused without a write of its own.
            for (var step = 0; step < Game.StepBurst; step++)
            {
                game.TryMove(player, GameDirection.East, turnOnly: false);
            }

            var isWallMoved = false;
            timeProvider.OnNextTimestamp(() => isWallMoved = Task.Run(() => wall.X = 20).Wait(TimeSpan.FromSeconds(2)));

            // Act
            game.TryMove(player, GameDirection.East, turnOnly: false);

            // Assert
            Assert.True(isWallMoved);
        }
        finally
        {
            await game.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenAWallIsMovedFromOutside_ThenMovementSeesItsNewPosition()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var wall = _context.AddWall(10, 10);
        await _context.Game.StartAsync(CancellationToken.None);
        try
        {
            // The first pass saves the join after the loop requested its own index rebuild. Querying the targets then
            // builds the index, with the wall still at 10, 10.
            await AsyncTestHelpers.WaitUntilAsync(() => _context.Root.WriteCount > 0, message: "The first pass did not run");
            _context.Game.GetTargetObjects(player);

            // Act
            wall.X = 2;
            wall.Y = 0;
            wall.Width = 1;
            _context.TimeProvider.Advance(Game.StepInterval);
            var result = _context.Game.TryMove(player, GameDirection.East, turnOnly: false);

            // Assert
            Assert.False(result.IsAccepted);
        }
        finally
        {
            await _context.Game.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenADoorIsOpenedFromOutside_ThenMovementSeesItOpen()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var door = _context.AddDoor(-1, 2);
        await _context.Game.StartAsync(CancellationToken.None);
        try
        {
            // See WhenAWallIsMovedFromOutside_ThenMovementSeesItsNewPosition: the index is built with the door closed.
            await AsyncTestHelpers.WaitUntilAsync(() => _context.Root.WriteCount > 0, message: "The first pass did not run");
            _context.Game.GetTargetObjects(player);

            // Act
            door.IsOpen = true;
            _context.TimeProvider.Advance(Game.StepInterval);
            var result = _context.Game.TryMove(player, GameDirection.North, turnOnly: false);

            // Assert
            Assert.True(result.IsAccepted);
        }
        finally
        {
            await _context.Game.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenTheGameRuns_ThenTheVoiceLoopMixesEveryFrameDuration()
    {
        // Arrange
        var talker = _context.AddConnectedPlayer("tab-1");
        _context.AddConnectedPlayer("tab-2", x: 20);
        var sink = new RecordingVoiceSink();
        using var registration = _context.Game.VoiceMixer.RegisterSink("tab-2", sink);
        _context.Game.SetMicrophone(new GameCaller(talker), pageId: 1, isOn: true);
        await _context.Game.StartAsync(CancellationToken.None);
        try
        {
            // Act
            await AsyncTestHelpers.WaitUntilAsync(() =>
            {
                _context.Game.VoiceMixer.Receive("tab-1", 1, VoiceFrames.Create(1000));
                _context.TimeProvider.Advance(GameVoiceSettings.FrameDuration);
                return sink.Count > 0;
            }, message: "The voice loop did not mix the frame");

            // Assert
            Assert.Equal(200, VoiceFrames.ReadSample(sink.Frames[0], 0));
        }
        finally
        {
            await _context.Game.StopAsync(CancellationToken.None);
        }
    }
}
