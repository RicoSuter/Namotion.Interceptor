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
}
