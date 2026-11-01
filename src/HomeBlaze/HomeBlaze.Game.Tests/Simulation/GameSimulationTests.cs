using HomeBlaze.Game.Simulation;
using HomeBlaze.Game.Tests.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace HomeBlaze.Game.Tests.Simulation;

public class GameSimulationTests
{
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly RecordingLogger _logger = new();

    private GameSimulation CreateSimulation() => new(_timeProvider, _logger);

    [Fact]
    public async Task WhenAnActionIsInvoked_ThenItRunsInTheNextTickOnTheSimulationThread()
    {
        // Arrange
        var simulation = CreateSimulation();
        var wasOnSimulation = false;
        var task = simulation.InvokeAsync(() => wasOnSimulation = GameSimulation.Current == simulation, CancellationToken.None);

        // Act
        Assert.False(task.IsCompleted);
        simulation.RunTick();

        // Assert
        Assert.True(await task);
        Assert.True(wasOnSimulation);
        Assert.Null(GameSimulation.Current);
    }

    [Fact]
    public void WhenActionsArePosted_ThenTheyRunInOrder()
    {
        // Arrange
        var simulation = CreateSimulation();
        var order = new List<int>();
        simulation.Post(() => order.Add(1));
        simulation.Post(() => order.Add(2));

        // Act
        simulation.RunTick();

        // Assert
        Assert.Equal([1, 2], order);
    }

    [Fact]
    public async Task WhenAnActionThrows_ThenOnlyItsTaskFailsAndLaterActionsRun()
    {
        // Arrange
        var simulation = CreateSimulation();
        var failing = simulation.InvokeAsync<int>(() => throw new InvalidOperationException("broken"), CancellationToken.None);
        var later = simulation.InvokeAsync(() => 42, CancellationToken.None);

        // Act
        simulation.RunTick();

        // Assert
        Assert.Equal("broken", (await Assert.ThrowsAsync<InvalidOperationException>(() => failing)).Message);
        Assert.Equal(42, await later);
    }

    [Fact]
    public async Task WhenAnInvocationIsCanceledBeforeItsTick_ThenItDoesNotRun()
    {
        // Arrange
        var simulation = CreateSimulation();
        using var cancellation = new CancellationTokenSource();
        var hasRun = false;
        var task = simulation.InvokeAsync(() => hasRun = true, cancellation.Token);

        // Act
        await cancellation.CancelAsync();
        simulation.RunTick();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.False(hasRun);
    }

    [Fact]
    public void WhenAPostedActionThrows_ThenItIsLoggedAndTheTickGoesOn()
    {
        // Arrange
        var simulation = CreateSimulation();
        var hasRun = false;
        simulation.Post(() => throw new InvalidOperationException("broken"));
        simulation.Post(() => hasRun = true);

        // Act
        simulation.RunTick();

        // Assert
        Assert.True(hasRun);
        Assert.Contains(_logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public void WhenAnActionKeepsFailingEveryTick_ThenOnlyTheFirstFailureWarns()
    {
        // Arrange
        var simulation = CreateSimulation();
        simulation.AddTickHandler(() => throw new InvalidOperationException("broken"));
        simulation.AddTickHandler(() => { });

        // Act
        simulation.RunTick();
        simulation.RunTick();
        simulation.RunTick();

        // Assert
        Assert.Single(_logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Equal(2, _logger.Entries.Count(entry => entry.Level == LogLevel.Debug));
    }

    [Fact]
    public void WhenAnActionFailsAgainAfterATickWithoutFailure_ThenItWarnsAgain()
    {
        // Arrange
        var simulation = CreateSimulation();
        var shouldFail = true;
        simulation.AddTickHandler(() =>
        {
            if (shouldFail)
            {
                throw new InvalidOperationException("broken");
            }
        });

        // Act
        simulation.RunTick();
        shouldFail = false;
        simulation.RunTick();
        shouldFail = true;
        simulation.RunTick();

        // Assert
        Assert.Equal(2, _logger.Entries.Count(entry => entry.Level == LogLevel.Warning));
    }

    [Fact]
    public void WhenTickHandlersAreRegistered_ThenTheyRunAfterTheActionsInRegistrationOrder()
    {
        // Arrange
        var simulation = CreateSimulation();
        var order = new List<string>();
        simulation.AddTickHandler(() => order.Add("first"));
        simulation.AddTickHandler(() => order.Add("second"));
        simulation.Post(() => order.Add("action"));

        // Act
        simulation.RunTick();

        // Assert
        Assert.Equal(["action", "first", "second"], order);
    }

    [Fact]
    public async Task WhenRunAsyncIsRunning_ThenItTicksEveryInterval()
    {
        // Arrange
        var simulation = CreateSimulation();
        using var cancellation = new CancellationTokenSource();
        var ticks = 0;
        var thirdTick = new TaskCompletionSource();
        simulation.AddTickHandler(() =>
        {
            if (++ticks == 3)
            {
                thirdTick.TrySetResult();
            }
        });
        var run = simulation.RunAsync(cancellation.Token);

        // Act
        while (!thirdTick.Task.IsCompleted)
        {
            _timeProvider.Advance(GameSimulationSettings.TickInterval);
            await Task.Yield();
        }

        await cancellation.CancelAsync();
        await run;

        // Assert
        Assert.True(ticks >= 3);
    }
}
