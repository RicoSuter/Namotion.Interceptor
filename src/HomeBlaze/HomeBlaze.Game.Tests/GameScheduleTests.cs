using HomeBlaze.Game.Tests.Testing;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Namotion.Interceptor.Testing;
using RecordingLogger = HomeBlaze.Game.Tests.Testing.RecordingLogger;

namespace HomeBlaze.Game.Tests;

public class GameScheduleTests
{
    private const string NotPartOfGameMessage = "The object is not part of this game.";

    private static readonly TimeSpan TickInterval = GameSimulationSettings.TickInterval;

    private readonly GameTestContext _context = new();

    [Fact]
    public void WhenAScheduleIsDue_ThenTheActionRunsAndItsWritesReachTheIndex()
    {
        // Arrange
        var door = _context.AddDoor(0, 0);

        // Scheduled in the tick that takes the object over, so due two ticks later.
        _context.AddObject("scheduler", new ScheduledObject
        {
            X = 20,
            Y = 20,
            OnAdded = scheduler => scheduler.ScheduleFor(door, 2 * TickInterval, () => door.IsOpen = true)
        });
        _context.Tick();
        var isFreeBeforeDue = _context.Game.IsFree(door.Area);

        // Act
        _context.Tick();

        // Assert
        Assert.False(isFreeBeforeDue);
        Assert.True(door.IsOpen);
        Assert.True(_context.Game.IsFree(door.Area));
    }

    [Fact]
    public async Task WhenTheObjectIsRemoved_ThenItsSchedulesAreCanceled()
    {
        // Arrange
        var removed = _context.AddObject("removed", CreateIncrementingObject(0, TimeSpan.FromSeconds(1)));
        var kept = _context.AddObject("kept", CreateIncrementingObject(10, TimeSpan.FromSeconds(1)));

        // Act
        await _context.RunAsync(_context.Game.RemoveAsync(removed, CancellationToken.None));
        _context.TimeProvider.Advance(TimeSpan.FromSeconds(2));
        _context.Tick();

        // Assert
        Assert.Equal(0, removed.RunCount);
        Assert.Equal(1, kept.RunCount);
    }

    [Fact]
    public async Task WhenTheObjectIsReplaced_ThenItsSchedulesAreCanceled()
    {
        // Arrange
        var replaced = _context.AddObject("replaced", CreateIncrementingObject(0, TimeSpan.FromSeconds(1)));

        // Act
        await _context.RunAsync(_context.Game.ReplaceAsync(replaced, [new ScheduledObject { X = 0, Y = 0 }], CancellationToken.None));
        _context.TimeProvider.Advance(TimeSpan.FromSeconds(2));
        _context.Tick();

        // Assert
        Assert.Equal(0, replaced.RunCount);
    }

    [Fact]
    public void WhenTheObjectIsTakenOutFromOutside_ThenItsSchedulesAreCanceled()
    {
        // Arrange
        var removed = _context.AddObject("removed", CreateIncrementingObject(0, TimeSpan.FromSeconds(1)));
        _context.Publish();

        // Act
        _context.Game.Objects = new Dictionary<string, GameObject>();
        _context.Tick();
        _context.TimeProvider.Advance(TimeSpan.FromSeconds(2));
        _context.Tick();

        // Assert
        Assert.Equal(0, removed.RunCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenScheduledFromAPageThreadAndDisposedBeforeTheTick_ThenTheActionNeverRuns(bool isRegistered)
    {
        // Arrange
        var scheduled = _context.AddObject("scheduled", new ScheduledObject { X = 0, Y = 0 });
        var schedule = scheduled.ScheduleIncrement(TimeSpan.FromSeconds(1));
        if (isRegistered)
        {
            _context.Tick();
        }

        // Act
        schedule.Dispose();
        _context.TimeProvider.Advance(TimeSpan.FromSeconds(2));
        _context.Tick(2);

        // Assert
        Assert.Equal(0, scheduled.RunCount);
    }

    [Fact]
    public void WhenScheduledFromAPageThread_ThenTheDelayStartsWithTheNextTick()
    {
        // Arrange
        var scheduled = _context.AddObject("scheduled", new ScheduledObject { X = 0, Y = 0 });
        scheduled.ScheduleIncrement(2 * TickInterval);

        // The first tick registers the schedule, due two ticks after it.
        _context.Tick(2);
        var runCountBeforeDue = scheduled.RunCount;

        // Act
        _context.Tick();

        // Assert
        Assert.Equal(0, runCountBeforeDue);
        Assert.Equal(1, scheduled.RunCount);
    }

    [Fact]
    public void WhenTheDelayIsNegative_ThenItThrows()
    {
        // Arrange
        var scheduled = _context.AddObject("scheduled", new ScheduledObject { X = 0, Y = 0 });

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => scheduled.ScheduleIncrement(TimeSpan.FromTicks(-1)));
    }

    [Fact]
    public void WhenTheGameStarts_ThenOnAddedToWorldRunsForEveryLoadedObject()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var root = new TestRoot(SubjectContextFactory.Create(new ServiceCollection()));
        var game = new Game(new FakeClientDirectory(), timeProvider, NullLogger.Instance);
        var first = new ScheduledObject { X = 0, Y = 0 };
        var second = new ScheduledObject { X = 10, Y = 0 };
        game.Objects = new Dictionary<string, GameObject> { ["first"] = first, ["second"] = second };
        root.Game = game;

        // Act
        game.StartForTest();
        timeProvider.Advance(TickInterval);
        game.RunTickForTest();

        // Assert
        Assert.Equal(1, first.AddedCount);
        Assert.Equal(1, second.AddedCount);
    }

    [Fact]
    public void WhenObjectsAreTakenOverFromOutside_ThenOnAddedToWorldRunsOnlyForTheNewOnes()
    {
        // Arrange
        var kept = _context.AddObject("kept", new ScheduledObject { X = 0, Y = 0 });

        // Act
        var added = _context.AddObject("added", new ScheduledObject { X = 10, Y = 0 });

        // Assert
        Assert.Equal(1, kept.AddedCount);
        Assert.Equal(1, added.AddedCount);
    }

    [Fact]
    public async Task WhenAnObjectIsPlacedOrAddedByAReplacement_ThenOnAddedToWorldRuns()
    {
        // Arrange
        var placed = CreateIncrementingObject(0, TickInterval);
        var replacement = CreateIncrementingObject(0, TickInterval);

        // Act
        await _context.RunAsync(_context.Game.PlaceAsync(placed, CancellationToken.None));
        _context.Publish();
        _context.Tick(2);
        await _context.RunAsync(_context.Game.ReplaceAsync(placed, [replacement], CancellationToken.None));
        _context.Publish();
        _context.Tick(2);

        // Assert: the hooks could schedule, so the object was part of the game when they ran.
        Assert.Equal(1, placed.AddedCount);
        Assert.Equal(1, placed.RunCount);
        Assert.Equal(1, replacement.AddedCount);
        Assert.Equal(1, replacement.RunCount);
    }

    [Fact]
    public async Task WhenAnObjectIsRemovedBeforeItWasPublished_ThenOnAddedToWorldNeverRuns()
    {
        // Arrange
        var placed = new ScheduledObject { X = 0, Y = 0 };
        await _context.RunAsync(_context.Game.PlaceAsync(placed, CancellationToken.None));

        // Act
        await _context.RunAsync(_context.Game.RemoveAsync(placed, CancellationToken.None));
        _context.Publish();
        _context.Tick();

        // Assert
        Assert.Equal(0, placed.AddedCount);
    }

    [Fact]
    public async Task WhenAPlacedObjectIsTakenOverBeforeItWasPublished_ThenOnAddedToWorldRunsOnce()
    {
        // Arrange
        var placed = new ScheduledObject { X = 0, Y = 0 };
        _context.Publish();
        await _context.RunAsync(_context.Game.PlaceAsync(placed, CancellationToken.None));

        // Act
        _context.Game.Objects = new Dictionary<string, GameObject>(_context.Game.Objects) { ["placed"] = placed };
        _context.Tick();
        _context.Publish();
        _context.Tick();

        // Assert
        Assert.Equal(1, placed.AddedCount);
    }

    [Fact]
    public void WhenOnAddedToWorldThrows_ThenTheObjectStaysAndTheTickGoesOn()
    {
        // Arrange
        var logger = new RecordingLogger();
        var context = new GameTestContext(logger);
        var throwing = new ScheduledObject { X = 0, Y = 0, OnAdded = _ => throw new InvalidOperationException("Broken.") };
        var other = CreateIncrementingObject(10, TickInterval);

        // Act
        context.Game.Objects = new Dictionary<string, GameObject> { ["throwing"] = throwing, ["other"] = other };
        context.Tick(2);

        // Assert
        Assert.Equal(1, throwing.AddedCount);
        Assert.Equal(1, other.AddedCount);
        Assert.Equal(1, other.RunCount);
        Assert.False(context.Game.IsFree(throwing.Area));
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains(nameof(ScheduledObject)));
    }

    [Fact]
    public void WhenSchedulingAnObjectOfAnotherGame_ThenItThrows()
    {
        // Arrange
        var otherContext = new GameTestContext();
        var foreign = otherContext.AddObject("foreign", new ScheduledObject { X = 0, Y = 0 });
        Exception? onSimulationThread = null;

        // Act
        var offSimulationThread = Record.Exception(() => _context.Game.Schedule(foreign, TimeSpan.Zero, () => { }));
        _context.AddObject("own", new ScheduledObject
        {
            X = 0,
            Y = 0,
            OnAdded = own => onSimulationThread = Record.Exception(() => own.ScheduleFor(foreign, TimeSpan.Zero, () => { }))
        });

        // Assert
        Assert.Equal(NotPartOfGameMessage, Assert.IsType<InvalidOperationException>(offSimulationThread).Message);
        Assert.Equal(NotPartOfGameMessage, Assert.IsType<InvalidOperationException>(onSimulationThread).Message);
    }

    [Fact]
    public async Task WhenTheGameStartsAgainAfterAStop_ThenOnAddedToWorldSetsUpTheSchedulesOnce()
    {
        // Arrange
        var scheduled = _context.AddObject("scheduled", CreateIncrementingObject(0, TimeSpan.FromSeconds(1)));
        await _context.Game.StartAsync(CancellationToken.None);
        await _context.Game.StopAsync(CancellationToken.None);

        // Act
        await _context.Game.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => scheduled.AddedCount == 2, "OnAddedToWorld did not run again on the restart");

            // Runs after the schedule of the restart, so a schedule left over from the first run would have run too.
            var isMarkerRun = false;
            scheduled.ScheduleFor(scheduled, TimeSpan.FromSeconds(3), () => Volatile.Write(ref isMarkerRun, true));
            await WaitUntilAsync(() => Volatile.Read(ref isMarkerRun), "The marker schedule did not run");

            // Assert
            Assert.Equal(1, scheduled.RunCount);
        }
        finally
        {
            await _context.Game.StopAsync(CancellationToken.None);
        }
    }

    private static ScheduledObject CreateIncrementingObject(int x, TimeSpan delay) => new()
    {
        X = x,
        Y = 0,
        OnAdded = scheduled => scheduled.ScheduleIncrement(delay)
    };

    // Once the game runs, its own loop ticks on the clock, so the test only advances the clock and never ticks itself.
    private Task WaitUntilAsync(Func<bool> condition, string message) =>
        AsyncTestHelpers.WaitUntilAsync(() =>
        {
            _context.TimeProvider.Advance(TickInterval);
            return condition();
        }, message: message);
}
