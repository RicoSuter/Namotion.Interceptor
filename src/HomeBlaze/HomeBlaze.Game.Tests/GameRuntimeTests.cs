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
    public async Task WhenAWallIsMovedFromOutside_ThenMovementSeesItsNewPosition()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var wall = _context.AddWall(10, 10);
        await _context.Game.StartAsync(CancellationToken.None);
        try
        {
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
