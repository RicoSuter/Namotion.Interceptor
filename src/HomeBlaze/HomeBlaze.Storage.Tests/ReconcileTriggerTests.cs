using HomeBlaze.Storage.Internal;

namespace HomeBlaze.Storage.Tests;

public class ReconcileTriggerTests
{
    private readonly ManualTimeProvider _timeProvider = new();
    private readonly List<(IReadOnlySet<string> NamedPaths, bool AllNamed)> _passes = [];
    private TaskCompletionSource? _runningPass;
    private Func<Task>? _failPass;

    [Fact]
    public void WhenPathChanges_ThenPassRunsAfterQuietPeriod()
    {
        // Arrange
        using var trigger = CreateTrigger();

        // Act
        trigger.NotifyChanged("Home.md");
        _timeProvider.Advance(ReconcileTrigger.QuietPeriod - TimeSpan.FromMilliseconds(1));
        var passesBeforeQuietPeriodEnds = _passes.Count;
        _timeProvider.Advance(TimeSpan.FromMilliseconds(1));

        // Assert
        Assert.Equal(0, passesBeforeQuietPeriodEnds);
        var pass = Assert.Single(_passes);
        Assert.Equal(["Home.md"], pass.NamedPaths);
        Assert.False(pass.AllNamed);
    }

    [Fact]
    public void WhenPathsKeepChanging_ThenQuietPeriodRestartsAndPassNamesAllOfThem()
    {
        // Arrange
        using var trigger = CreateTrigger();

        // Act
        trigger.NotifyChanged("First.md");
        _timeProvider.Advance(TimeSpan.FromMilliseconds(800));
        trigger.NotifyChanged("Second.md", "Old.md");
        _timeProvider.Advance(TimeSpan.FromMilliseconds(800));
        var passesBeforeQuietPeriodEnds = _passes.Count;
        _timeProvider.Advance(TimeSpan.FromMilliseconds(200));

        // Assert
        Assert.Equal(0, passesBeforeQuietPeriodEnds);
        var pass = Assert.Single(_passes);
        Assert.Equal(["First.md", "Old.md", "Second.md"], pass.NamedPaths.Order());
    }

    [Fact]
    public void WhenPathsNeverStopChanging_ThenPassRunsAfterMaximumDelay()
    {
        // Arrange
        using var trigger = CreateTrigger();

        // Act
        for (var elapsed = TimeSpan.Zero; elapsed < ReconcileTrigger.MaximumDelay; elapsed += TimeSpan.FromMilliseconds(500))
        {
            trigger.NotifyChanged("Busy.md");
            _timeProvider.Advance(TimeSpan.FromMilliseconds(500));
        }

        // Assert
        Assert.Single(_passes);
    }

    [Fact]
    public void WhenEventsAreLost_ThenPassRunsAtOnceWithEveryFileNamed()
    {
        // Arrange
        using var trigger = CreateTrigger();

        // Act
        trigger.NotifyEventsLost();
        _timeProvider.Advance(TimeSpan.Zero);

        // Assert
        var pass = Assert.Single(_passes);
        Assert.True(pass.AllNamed);
    }

    [Fact]
    public void WhenPeriodicIntervalElapses_ThenPassRunsWithoutNamedPaths()
    {
        // Arrange
        using var trigger = CreateTrigger(periodicInterval: TimeSpan.FromMinutes(5));

        // Act
        _timeProvider.Advance(TimeSpan.FromMinutes(5));
        var passesAfterFirstInterval = _passes.Count;
        _timeProvider.Advance(TimeSpan.FromMinutes(5));

        // Assert
        Assert.Equal(1, passesAfterFirstInterval);
        Assert.Equal(2, _passes.Count);
        Assert.All(_passes, pass =>
        {
            Assert.Empty(pass.NamedPaths);
            Assert.False(pass.AllNamed);
        });
    }

    [Fact]
    public void WhenPeriodicIntervalIsZero_ThenNoPeriodicPassRuns()
    {
        // Arrange
        using var trigger = CreateTrigger(periodicInterval: TimeSpan.Zero);

        // Act
        _timeProvider.Advance(TimeSpan.FromHours(1));

        // Assert
        Assert.Empty(_passes);
    }

    [Fact]
    public void WhenPeriodicIntervalExceedsWhatTimerAccepts_ThenPeriodicPassIsStillScheduled()
    {
        // Act
        using var trigger = CreateTrigger(periodicInterval: TimeSpan.FromSeconds(int.MaxValue));

        // Assert
        Assert.Equal(1, _timeProvider.ArmedTimerCount);
    }

    [Fact]
    public void WhenPeriodicIntervalElapsesDuringPass_ThenExactlyOnePassFollows()
    {
        // Arrange
        using var trigger = CreateTrigger(periodicInterval: TimeSpan.FromMinutes(5));
        _runningPass = new TaskCompletionSource();
        trigger.NotifyChanged("First.md");
        _timeProvider.Advance(ReconcileTrigger.QuietPeriod);

        // Act
        _timeProvider.Advance(TimeSpan.FromMinutes(10));
        var passesWhileFirstRuns = _passes.Count;

        var firstPass = _runningPass;
        _runningPass = null;
        firstPass.SetResult();
        _timeProvider.Advance(TimeSpan.FromMinutes(1));

        // Assert
        Assert.Equal(1, passesWhileFirstRuns);
        Assert.Equal(2, _passes.Count);
        Assert.Empty(_passes[1].NamedPaths);
        Assert.False(_passes[1].AllNamed);
    }

    [Fact]
    public void WhenTriggerIsDisposedDuringPass_ThenNoPassFollows()
    {
        // Arrange
        var trigger = CreateTrigger(periodicInterval: TimeSpan.FromMinutes(5));
        _runningPass = new TaskCompletionSource();
        trigger.NotifyChanged("First.md");
        _timeProvider.Advance(ReconcileTrigger.QuietPeriod);
        trigger.NotifyChanged("Second.md");

        // Act
        trigger.Dispose();
        _runningPass.SetResult();
        _timeProvider.Advance(TimeSpan.FromMinutes(10));

        // Assert
        Assert.Single(_passes);
        Assert.Equal(0, _timeProvider.ArmedTimerCount);
    }

    [Fact]
    public void WhenPathsChangeDuringPass_ThenExactlyOnePassFollows()
    {
        // Arrange
        using var trigger = CreateTrigger();
        _runningPass = new TaskCompletionSource();
        trigger.NotifyChanged("First.md");
        _timeProvider.Advance(ReconcileTrigger.QuietPeriod);

        // Act
        trigger.NotifyChanged("Second.md");
        trigger.NotifyChanged("Third.md");
        _timeProvider.Advance(ReconcileTrigger.MaximumDelay);
        var passesWhileFirstRuns = _passes.Count;

        var firstPass = _runningPass;
        _runningPass = null;
        firstPass.SetResult();
        _timeProvider.Advance(ReconcileTrigger.MaximumDelay);

        // Assert
        Assert.Equal(1, passesWhileFirstRuns);
        Assert.Equal(2, _passes.Count);
        Assert.Equal(["Second.md", "Third.md"], _passes[1].NamedPaths.Order());
    }

    [Fact]
    public void WhenTriggerIsDisposed_ThenNoPassRuns()
    {
        // Arrange
        var trigger = CreateTrigger(periodicInterval: TimeSpan.FromMinutes(5));
        trigger.NotifyChanged("Home.md");

        // Act
        trigger.Dispose();
        trigger.NotifyChanged("Other.md");
        _timeProvider.Advance(TimeSpan.FromMinutes(10));

        // Assert
        Assert.Empty(_passes);
    }

    [Fact]
    public void WhenPathChangesDuringPass_ThenNamedPathsOfRunningPassStayAsHandedOver()
    {
        // Arrange
        using var trigger = CreateTrigger();
        _runningPass = new TaskCompletionSource();
        trigger.NotifyChanged("First.md");
        _timeProvider.Advance(ReconcileTrigger.QuietPeriod);

        // Act
        trigger.NotifyChanged("Second.md");

        // Assert
        Assert.Equal(["First.md"], Assert.Single(_passes).NamedPaths);
    }

    [Fact]
    public void WhenEventsWereLost_ThenOnlyTheNextPassNamesEveryFile()
    {
        // Arrange
        using var trigger = CreateTrigger();
        trigger.NotifyEventsLost();
        _timeProvider.Advance(TimeSpan.Zero);

        // Act
        trigger.NotifyChanged("Home.md");
        _timeProvider.Advance(ReconcileTrigger.QuietPeriod);

        // Assert
        Assert.Equal(2, _passes.Count);
        Assert.True(_passes[0].AllNamed);
        Assert.False(_passes[1].AllNamed);
        Assert.Equal(["Home.md"], _passes[1].NamedPaths);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WhenPassFails_ThenLaterChangeStillRunsPass(bool failsBeforeReturningTask)
    {
        // Arrange
        using var trigger = CreateTrigger();
        var failure = new InvalidOperationException("failed");
        _failPass = failsBeforeReturningTask ? () => throw failure : () => Task.FromException(failure);
        trigger.NotifyChanged("First.md");
        _timeProvider.Advance(ReconcileTrigger.QuietPeriod);
        _failPass = null;

        // Act
        trigger.NotifyChanged("Second.md");
        _timeProvider.Advance(ReconcileTrigger.QuietPeriod);

        // Assert
        Assert.Equal(2, _passes.Count);
        Assert.Equal(["Second.md"], _passes[1].NamedPaths);
    }

    [Fact]
    public async Task WhenTriggerIsCreatedWithinFlow_ThenItsPassesDoNotCarryThatFlow()
    {
        // Arrange
        var flow = new AsyncLocal<string?> { Value = "creator" };
        var flowOfPass = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        // The system clock: only a real timer captures the flow it is created in.
        using var trigger = new ReconcileTrigger(
            (_, _) =>
            {
                flowOfPass.TrySetResult(flow.Value);
                return Task.CompletedTask;
            },
            TimeSpan.Zero);

        // Act
        trigger.NotifyEventsLost();

        // Assert
        Assert.Null(await flowOfPass.Task.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    private ReconcileTrigger CreateTrigger(TimeSpan? periodicInterval = null)
        => new(
            (namedPaths, allNamed) =>
            {
                _passes.Add((namedPaths, allNamed));
                return _failPass?.Invoke() ?? _runningPass?.Task ?? Task.CompletedTask;
            },
            periodicInterval ?? TimeSpan.Zero,
            _timeProvider);
}
