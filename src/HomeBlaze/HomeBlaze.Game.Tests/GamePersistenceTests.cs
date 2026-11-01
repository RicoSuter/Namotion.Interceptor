using HomeBlaze.Game.Tests.Testing;

namespace HomeBlaze.Game.Tests;

public class GamePersistenceTests
{
    private readonly GameTestContext _context = new();

    [Fact]
    public async Task WhenAPlayerMovesSeveralTimesWithinTheSaveInterval_ThenOneWriteHappensAtTheInterval()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        await _context.SaveAsync();
        var writesBefore = _context.Root.WriteCount;

        // Act
        await _context.StepAsync("tab-1", GameDirection.East);
        await _context.StepAsync("tab-1", GameDirection.East);
        var writesAfterMoves = _context.Root.WriteCount;
        await _context.SaveAsync();

        // Assert
        Assert.Equal(writesBefore, writesAfterMoves);
        Assert.Equal(writesBefore + 1, _context.Root.WriteCount);
    }

    [Fact]
    public async Task WhenNothingChanged_ThenNothingIsWritten()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        await _context.SaveAsync();
        var writesBefore = _context.Root.WriteCount;

        // Act
        await _context.SaveAsync();
        await _context.SaveAsync();

        // Assert
        Assert.Equal(writesBefore, _context.Root.WriteCount);
    }

    [Fact]
    public async Task WhenWritingFails_ThenTheNextIntervalWritesAgain()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        await _context.SaveAsync();
        await _context.StepAsync("tab-1", GameDirection.East);
        _context.Root.FailsToWrite = true;
        await _context.SaveAsync();
        var writesBefore = _context.Root.WriteCount;
        _context.Root.FailsToWrite = false;

        // Act
        await _context.SaveAsync();
        await _context.SaveAsync();

        // Assert
        Assert.Equal(writesBefore + 1, _context.Root.WriteCount);
    }

    [Fact]
    public async Task WhenAnObjectIsWrittenFromOutside_ThenItIsSavedWithinTheInterval()
    {
        // Arrange
        var door = _context.AddDoor(10, 10);
        await _context.SaveAsync();
        var writesBefore = _context.Root.WriteCount;

        // Act
        door.IsOpen = true;
        await _context.SaveAsync();

        // Assert
        Assert.Equal(writesBefore + 1, _context.Root.WriteCount);
    }

    [Fact]
    public async Task WhenAPlayerStepsWhileASaveRuns_ThenTheStepIsPublishedAfterTheSave()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        await _context.SaveAsync();
        await _context.StepAsync("tab-1", GameDirection.East);
        var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _context.Root.WriteBlocker = blocker.Task;

        // Ticks without GameTestContext.Tick, which would wait for the held save.
        _context.TimeProvider.Advance(GameSimulationSettings.SaveInterval);
        _context.Game.RunTickForTest();
        var isSaveRunning = !_context.Game.SaveTask.IsCompleted;

        // Act
        _context.TimeProvider.Advance(Game.StepInterval);
        var step = _context.Game.StepAsync("tab-1", GameDirection.East, turnOnly: false);
        _context.Game.RunTickForTest();
        _context.TimeProvider.Advance(GameSimulationSettings.PublishInterval);
        _context.Game.RunTickForTest();
        var xDuringSave = player.X;
        blocker.SetResult();
        await _context.Game.SaveTask;
        _context.Tick();

        // Assert
        Assert.True(isSaveRunning);
        Assert.Equal(2, (await step)!.Value.X);
        Assert.Equal(1, xDuringSave);
        Assert.Equal(2, player.X);
    }

    [Fact]
    public async Task WhenTheGameStops_ThenPendingChangesArePublishedAndWritten()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        await _context.SaveAsync();
        var writesBefore = _context.Root.WriteCount;
        await _context.StepAsync("tab-1", GameDirection.East);

        // Act
        await _context.Game.StopAsync(CancellationToken.None);

        // Assert
        Assert.Equal(writesBefore + 1, _context.Root.WriteCount);
        Assert.Equal(1, player.X);
    }

    [Fact]
    public async Task WhenTheGameStopsWithACanceledToken_ThenStopDoesNotThrow()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        await _context.Game.StartAsync(CancellationToken.None);

        // Act
        var exception = await Record.ExceptionAsync(() => _context.Game.StopAsync(new CancellationToken(canceled: true)));

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public async Task WhenWritingFails_ThenTheGameKeepsRunning()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        _context.Root.FailsToWrite = true;

        // Act
        var exception = await Record.ExceptionAsync(async () =>
        {
            await _context.StepAsync("tab-1", GameDirection.East);
            await _context.SaveAsync();
        });
        _context.Publish();

        // Assert
        Assert.Null(exception);
        Assert.Equal(1, player.X);
    }
}
