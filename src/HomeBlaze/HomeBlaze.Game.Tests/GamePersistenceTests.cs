using HomeBlaze.Game.Tests.Testing;
using Namotion.Interceptor.Testing;

namespace HomeBlaze.Game.Tests;

public class GamePersistenceTests
{
    private readonly GameTestContext _context = new();

    [Fact]
    public async Task WhenAPlayerMovesSeveralTimes_ThenOneWriteHappensAtTheNextFlush()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        await _context.Game.FlushPendingSaveAsync(CancellationToken.None);
        var writesBefore = _context.Root.WriteCount;

        // Act
        _context.Game.TryMove(player, GameDirection.East, turnOnly: false);
        _context.Game.TryMove(player, GameDirection.East, turnOnly: false);
        var writesAfterMoves = _context.Root.WriteCount;
        await _context.Game.FlushPendingSaveAsync(CancellationToken.None);
        await _context.Game.FlushPendingSaveAsync(CancellationToken.None);

        // Assert
        Assert.Equal(writesBefore, writesAfterMoves);
        Assert.Equal(writesBefore + 1, _context.Root.WriteCount);
    }

    [Fact]
    public async Task WhenTheGameStops_ThenPendingMovesAreWritten()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        await _context.Game.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => _context.Root.WriteCount > 0 && !_context.Game.HasPendingSave, message: "The join was not settled");
        var writesBefore = _context.Root.WriteCount;
        _context.Game.TryMove(player, GameDirection.East, turnOnly: false);

        // Act
        await _context.Game.StopAsync(CancellationToken.None);

        // Assert
        Assert.Equal(writesBefore + 1, _context.Root.WriteCount);
    }

    [Fact]
    public async Task WhenWritingFails_ThenTheFlushDoesNotThrow()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        _context.Root.FailsToWrite = true;
        _context.Game.TryMove(player, GameDirection.East, turnOnly: false);

        // Act
        var exception = await Record.ExceptionAsync(() => _context.Game.FlushPendingSaveAsync(CancellationToken.None));

        // Assert
        Assert.Null(exception);
        Assert.Equal(1, player.X);
    }
}
