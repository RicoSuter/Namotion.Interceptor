using HomeBlaze.Game.Tests.Testing;
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
    public async Task WhenAWallIsMovedFromOutside_ThenMovementSeesItsNewPosition()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var wall = _context.AddWall(10, 10);
        await _context.Game.StartAsync(CancellationToken.None);
        try
        {
            // StartAsync returns once the loop reached its first wait, so its own index rebuild request is done. Querying
            // the targets builds the index now, with the wall still at 10, 10.
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
}
