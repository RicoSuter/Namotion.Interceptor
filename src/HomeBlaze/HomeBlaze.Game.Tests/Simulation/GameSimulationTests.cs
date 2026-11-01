using HomeBlaze.Game.Simulation;
using HomeBlaze.Game.Tests.Testing;
using RecordingLogger = HomeBlaze.Game.Tests.Testing.RecordingLogger;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace HomeBlaze.Game.Tests.Simulation;

public class GameSimulationTests
{
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly RecordingLogger _logger = new();

    private int _ticks;

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
    public async Task WhenAnInvocationIsCanceledBeforeItsTick_ThenItsTaskIsCanceledAtOnceAndItDoesNotRun()
    {
        // Arrange
        var simulation = CreateSimulation();
        using var cancellation = new CancellationTokenSource();
        var hasRun = false;
        var task = simulation.InvokeAsync(() => hasRun = true, cancellation.Token);

        // Act
        await cancellation.CancelAsync();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        simulation.RunTick();
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
    public async Task WhenRunAsyncIsRunning_ThenItTicksOncePerInterval()
    {
        // Arrange
        var simulation = CreateSimulation();
        using var cancellation = new CancellationTokenSource();
        simulation.AddTickHandler(() => Interlocked.Increment(ref _ticks));
        var run = simulation.RunAsync(cancellation.Token);

        // Act & Assert
        for (var expected = 1; expected <= 3; expected++)
        {
            _timeProvider.Advance(GameSimulationSettings.TickInterval);
            await Namotion.Interceptor.Testing.AsyncTestHelpers.WaitUntilAsync(
                () => Volatile.Read(ref _ticks) >= expected || run.IsCompleted,
                timeout: TimeSpan.FromSeconds(10),
                pollInterval: TimeSpan.FromMilliseconds(1));
            Assert.False(run.IsCompleted);
            Assert.Equal(expected, Volatile.Read(ref _ticks));
        }

        await cancellation.CancelAsync();
        await run;
    }

    [Fact]
    public async Task WhenRunAsyncStops_ThenQueuedWorkRunsInAFinalTickAndLaterWorkIsRefused()
    {
        // Arrange
        var simulation = CreateSimulation();
        var order = new List<string>();
        simulation.AddTickHandler(() => order.Add("handler"));
        simulation.Post(() => order.Add("posted"));
        var invoked = simulation.InvokeAsync(() => 42, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // Act
        await simulation.RunAsync(cancellation.Token);
        simulation.Post(() => order.Add("late"));
        simulation.RunTick();
        var late = simulation.InvokeAsync(() => 1, CancellationToken.None);

        // Assert
        Assert.Equal(42, await invoked);
        Assert.Equal(["posted", "handler", "handler"], order);
        Assert.Equal("The game has stopped.", (await Assert.ThrowsAsync<InvalidOperationException>(() => late)).Message);
    }

    [Fact]
    public void WhenAnActionPostsDuringATick_ThenItRunsInTheNextTick()
    {
        // Arrange
        var simulation = CreateSimulation();
        var order = new List<string>();
        simulation.Post(() =>
        {
            order.Add("outer");
            simulation.Post(() => order.Add("inner"));
        });

        // Act
        simulation.RunTick();
        var afterFirstTick = order.ToArray();
        simulation.RunTick();

        // Assert
        Assert.Equal(["outer"], afterFirstTick);
        Assert.Equal(["outer", "inner"], order);
    }

    [Fact]
    public async Task WhenInvokedOnTheSimulationThread_ThenTheFunctionRunsInlineAndTheTaskIsCompleted()
    {
        // Arrange
        var simulation = CreateSimulation();
        var order = new List<string>();
        Task<int>? result = null;
        Task<int>? failed = null;
        Task<int>? canceled = null;
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        simulation.Post(() =>
        {
            result = simulation.InvokeAsync(() => { order.Add("inline"); return 7; }, CancellationToken.None);
            order.Add("after");
            failed = simulation.InvokeAsync<int>(() => throw new InvalidOperationException("broken"), CancellationToken.None);
            canceled = simulation.InvokeAsync(() => 1, cancellation.Token);
        });

        // Act
        simulation.RunTick();

        // Assert
        Assert.Equal(["inline", "after"], order);
        Assert.Equal(7, await result!);
        Assert.True(failed!.IsFaulted);
        Assert.True(canceled!.IsCanceled);
        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public async Task WhenInvokedAfterTheRunStoppedOnTheSimulationThread_ThenNothingIsStranded()
    {
        // Arrange
        var simulation = CreateSimulation();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await simulation.RunAsync(cancellation.Token);

        // Act
        var task = simulation.InvokeAsync(() => 1, CancellationToken.None);

        // Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => task);
    }

    [Fact]
    public void WhenAnActionThrows_ThenCurrentIsNullAfterTheTick()
    {
        // Arrange
        var simulation = CreateSimulation();
        simulation.Post(() => throw new InvalidOperationException("broken"));

        // Act
        simulation.RunTick();

        // Assert
        Assert.Null(GameSimulation.Current);
    }

    [Fact]
    public void WhenATickRunsInsideAnotherTick_ThenTheOuterSimulationIsCurrentAgain()
    {
        // Arrange
        var outer = CreateSimulation();
        var inner = CreateSimulation();
        GameSimulation? duringInner = null;
        GameSimulation? afterInner = null;
        inner.Post(() => duringInner = GameSimulation.Current);
        outer.Post(() =>
        {
            inner.RunTick();
            afterInner = GameSimulation.Current;
        });

        // Act
        outer.RunTick();

        // Assert
        Assert.Same(inner, duringInner);
        Assert.Same(outer, afterInner);
        Assert.Null(GameSimulation.Current);
    }

    [Fact]
    public void WhenATickHandlerIsAddedAfterTheFirstTick_ThenItThrows()
    {
        // Arrange
        var simulation = CreateSimulation();
        simulation.RunTick();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => simulation.AddTickHandler(() => { }));
    }

    [Fact]
    public void WhenArgumentsAreNull_ThenTheyAreRejected()
    {
        // Arrange
        var simulation = CreateSimulation();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => simulation.Post(null!));
        Assert.Throws<ArgumentNullException>(() => { _ = simulation.InvokeAsync<int>(null!, CancellationToken.None); });
        Assert.Throws<ArgumentNullException>(() => simulation.AddTickHandler(null!));
    }

    [Fact]
    public void WhenAFailureStreakContinuesForAMinute_ThenItWarnsAgainWithTheSuppressedCount()
    {
        // Arrange
        var simulation = CreateSimulation();
        simulation.AddTickHandler(() => throw new InvalidOperationException("broken"));
        simulation.RunTick();
        simulation.RunTick();
        simulation.RunTick();

        // Act
        _timeProvider.Advance(TimeSpan.FromMinutes(1));
        simulation.RunTick();

        // Assert
        var warnings = _logger.Entries.Where(entry => entry.Level == LogLevel.Warning).ToArray();
        Assert.Equal(2, warnings.Length);
        Assert.Contains("2", warnings[1].Message);
    }

    [Fact]
    public void WhenAnActionFails_ThenTheLogNamesWhatFailed()
    {
        // Arrange
        var simulation = CreateSimulation();
        simulation.AddTickHandler(FailingHandler);

        // Act
        simulation.RunTick();

        // Assert
        Assert.Contains(_logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains(nameof(FailingHandler)));
    }

    private static void FailingHandler() => throw new InvalidOperationException("broken");
}
