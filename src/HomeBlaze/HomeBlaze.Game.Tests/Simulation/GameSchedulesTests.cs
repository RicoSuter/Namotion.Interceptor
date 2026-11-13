using HomeBlaze.Game.Simulation;
using HomeBlaze.Game.Tests.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace HomeBlaze.Game.Tests.Simulation;

public class GameSchedulesTests
{
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly RecordingLogger _logger = new();

    [Fact]
    public void WhenTheDelayPassed_ThenTheActionRunsInTheNextTickBeforeThePostedActions()
    {
        // Arrange
        var simulation = new GameSimulation(_timeProvider, _logger);
        var order = new List<string>();
        simulation.Post(() => simulation.Schedule(owner: this, TimeSpan.FromSeconds(1), () => order.Add("scheduled")));
        simulation.RunTick();
        simulation.Post(() => order.Add("posted"));

        // Act
        _timeProvider.Advance(TimeSpan.FromSeconds(1));
        simulation.RunTick();

        // Assert
        Assert.Equal(["scheduled", "posted"], order);
    }

    [Fact]
    public void WhenTheSimulationRestartsAfterAStop_ThenTheSchedulesOfTheFormerRunNeverRun()
    {
        // Arrange
        var simulation = new GameSimulation(_timeProvider, _logger);
        var hasRun = false;
        simulation.Post(() => simulation.Schedule(this, TimeSpan.FromSeconds(1), () => hasRun = true));
        simulation.RunTick();
        simulation.Stop();
        simulation.RunExclusive(() => simulation.Schedule(this, TimeSpan.FromSeconds(1), () => hasRun = true));

        // Act
        simulation.Restart();
        _timeProvider.Advance(TimeSpan.FromSeconds(1));
        simulation.RunTick();

        // Assert
        Assert.False(hasRun);
    }

    [Fact]
    public void WhenTheDelayHasNotPassed_ThenTheActionWaits()
    {
        // Arrange
        var simulation = new GameSimulation(_timeProvider, _logger);
        var hasRun = false;
        simulation.Post(() => simulation.Schedule(this, TimeSpan.FromSeconds(1), () => hasRun = true));
        simulation.RunTick();

        // Act
        _timeProvider.Advance(TimeSpan.FromMilliseconds(999));
        simulation.RunTick();

        // Assert
        Assert.False(hasRun);
    }

    [Fact]
    public void WhenTheScheduleIsDisposed_ThenTheActionNeverRuns()
    {
        // Arrange
        var simulation = new GameSimulation(_timeProvider, _logger);
        var hasRun = false;
        IDisposable? schedule = null;
        simulation.Post(() => schedule = simulation.Schedule(this, TimeSpan.FromSeconds(1), () => hasRun = true));
        simulation.RunTick();

        // Act
        schedule!.Dispose();
        _timeProvider.Advance(TimeSpan.FromSeconds(1));
        simulation.RunTick();

        // Assert
        Assert.False(hasRun);
    }

    [Fact]
    public void WhenTheOwnersSchedulesAreCanceled_ThenNoneOfThemRuns()
    {
        // Arrange
        var simulation = new GameSimulation(_timeProvider, _logger);
        var owner = new object();
        var count = 0;
        simulation.Post(() =>
        {
            simulation.Schedule(owner, TimeSpan.FromSeconds(1), () => count++);
            simulation.Schedule(owner, TimeSpan.FromSeconds(2), () => count++);
            simulation.Schedule(this, TimeSpan.FromSeconds(1), () => count += 10);
        });
        simulation.RunTick();

        // Act
        simulation.Post(() => simulation.CancelSchedules(owner));
        simulation.RunTick();
        _timeProvider.Advance(TimeSpan.FromSeconds(2));
        simulation.RunTick();

        // Assert
        Assert.Equal(10, count);
    }

    [Fact]
    public void WhenAScheduledActionThrows_ThenItIsLoggedAndTheTickGoesOn()
    {
        // Arrange
        var simulation = new GameSimulation(_timeProvider, _logger);
        var hasRun = false;
        simulation.Post(() =>
        {
            simulation.Schedule(this, TimeSpan.Zero, () => throw new InvalidOperationException("broken"));
            simulation.Schedule(this, TimeSpan.Zero, () => hasRun = true);
        });
        simulation.RunTick();

        // Act
        simulation.RunTick();

        // Assert
        Assert.True(hasRun);
        Assert.Contains(_logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public void WhenSchedulesDueAtTheSameTimeExist_ThenTheyRunInTheOrderTheyWereAdded()
    {
        // Arrange
        var simulation = new GameSimulation(_timeProvider, _logger);
        var order = new List<int>();
        simulation.Post(() =>
        {
            simulation.Schedule(this, TimeSpan.Zero, () => order.Add(1));
            simulation.Schedule(this, TimeSpan.Zero, () => order.Add(2));
            simulation.Schedule(this, TimeSpan.Zero, () => order.Add(3));
        });
        simulation.RunTick();

        // Act
        simulation.RunTick();

        // Assert
        Assert.Equal([1, 2, 3], order);
    }

    [Fact]
    public void WhenScheduledOutsideTheSimulationThread_ThenItThrows()
    {
        // Arrange
        var simulation = new GameSimulation(_timeProvider, _logger);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => simulation.Schedule(this, TimeSpan.Zero, () => { }));
    }

    [Fact]
    public void WhenScheduledOffTheSimulationThread_ThenCancelingThrows()
    {
        // Arrange
        var simulation = new GameSimulation(_timeProvider, _logger);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => simulation.CancelSchedules(this));
    }

    [Fact]
    public void WhenTheOwnersSchedulesWereCanceled_ThenANewScheduleOfTheOwnerRuns()
    {
        // Arrange
        var simulation = new GameSimulation(_timeProvider, _logger);
        var count = 0;
        simulation.Post(() => simulation.Schedule(this, TimeSpan.FromSeconds(1), () => count += 1));
        simulation.RunTick();
        simulation.Post(() =>
        {
            simulation.CancelSchedules(this);
            simulation.Schedule(this, TimeSpan.FromSeconds(1), () => count += 10);
        });
        simulation.RunTick();

        // Act
        _timeProvider.Advance(TimeSpan.FromSeconds(1));
        simulation.RunTick();

        // Assert
        Assert.Equal(10, count);
    }

    [Fact]
    public void WhenAScheduledActionSchedulesWithZeroDelay_ThenItRunsInTheNextTick()
    {
        // Arrange
        var simulation = new GameSimulation(_timeProvider, _logger);
        var order = new List<string>();
        simulation.Post(() => simulation.Schedule(this, TimeSpan.Zero, () =>
        {
            order.Add("outer");
            simulation.Schedule(this, TimeSpan.Zero, () => order.Add("inner"));
        }));
        simulation.RunTick();

        // Act
        simulation.RunTick();
        var afterSecondTick = order.ToArray();
        simulation.RunTick();

        // Assert
        Assert.Equal(["outer"], afterSecondTick);
        Assert.Equal(["outer", "inner"], order);
    }

    [Fact]
    public void WhenAnActionSchedulesWithZeroDelay_ThenItRunsInTheNextTick()
    {
        // Arrange
        var simulation = new GameSimulation(_timeProvider, _logger);
        var hasRun = false;
        simulation.Post(() => simulation.Schedule(this, TimeSpan.Zero, () => hasRun = true));

        // Act
        simulation.RunTick();
        var afterFirstTick = hasRun;
        simulation.RunTick();

        // Assert
        Assert.False(afterFirstTick);
        Assert.True(hasRun);
    }

    [Fact]
    public void WhenTheDelayIsNegative_ThenItThrows()
    {
        // Arrange
        var simulation = new GameSimulation(_timeProvider, _logger);
        Exception? exception = null;
        simulation.Post(() => exception = Record.Exception(() => simulation.Schedule(this, TimeSpan.FromSeconds(-1), () => { })));

        // Act
        simulation.RunTick();

        // Assert
        Assert.IsType<ArgumentOutOfRangeException>(exception);
    }

    [Fact]
    public void WhenTheDelayIsTheMaximum_ThenTheActionNeverRunsAndNothingOverflows()
    {
        // Arrange
        var simulation = new GameSimulation(_timeProvider, _logger);
        var hasRun = false;
        simulation.Post(() => simulation.Schedule(this, TimeSpan.MaxValue, () => hasRun = true));
        simulation.RunTick();

        // Act
        _timeProvider.Advance(TimeSpan.FromDays(365));
        simulation.RunTick();

        // Assert
        Assert.False(hasRun);
        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public void WhenArgumentsAreNull_ThenScheduleRejectsThem()
    {
        // Arrange
        var simulation = new GameSimulation(_timeProvider, _logger);

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => simulation.Schedule(null!, TimeSpan.Zero, () => { }));
        Assert.Throws<ArgumentNullException>(() => simulation.Schedule(this, TimeSpan.Zero, null!));
    }
}
