using HomeBlaze.Automation.Blazor.Runs;
using HomeBlaze.Automation.Blazor.Tests.Testing;
using HomeBlaze.History.Abstractions;

namespace HomeBlaze.Automation.Blazor.Tests.Runs;

public class RunHistoryTests
{
    private const string Finished = "/Washer/Rules[finished]";
    private const string Started = "/Washer/Rules[started]";
    private static readonly DateTimeOffset Start = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    private static TestHistoryStore CreateStore() => new(new HistoryCoverage(Start.AddDays(-1), Start.AddDays(1)));

    private static Task<IReadOnlyList<RunEntry>> LoadAsync(TestHistoryStore store, int maxRuns = 10, params RunSource[] rules) =>
        RunHistory.LoadAsync(
            [store], rules.Length > 0 ? rules : [new RunSource("Finished", Finished)], Start, Start.AddMinutes(10), maxRuns, CancellationToken.None);

    [Fact]
    public async Task WhenTwoIdenticalRunsFollowEachOther_ThenBothAreListedWithTheirOutcomeNewestFirst()
    {
        // Arrange
        var store = CreateStore()
            .Add(Finished + "/RunCount", Start, 0)
            .Add(Finished + "/RunCount", Start.AddMinutes(1), 1)
            .AddText(Finished + "/LastRunOutcome", Start.AddMinutes(1), "DryRun")
            .AddText(Finished + "/LastRunMessage", Start.AddMinutes(1), "Would run ChangeState(Idle)")
            .Add(Finished + "/RunCount", Start.AddMinutes(2), 2);

        // Act
        var runs = await LoadAsync(store);

        // Assert
        Assert.Equal(
            [
                new RunEntry(Start.AddMinutes(2), "Finished", AutomationRunOutcome.DryRun, "Would run ChangeState(Idle)"),
                new RunEntry(Start.AddMinutes(1), "Finished", AutomationRunOutcome.DryRun, "Would run ChangeState(Idle)")
            ],
            runs.ToArray());
    }

    [Fact]
    public async Task WhenTheCountIsRestoredAfterAStart_ThenTheRestoreIsNoRun()
    {
        // Arrange
        var store = CreateStore()
            .Add(Finished + "/RunCount", Start, 5)
            .Add(Finished + "/RunCount", Start.AddMinutes(1), null)
            .Add(Finished + "/RunCount", Start.AddMinutes(2), 5)
            .Add(Finished + "/RunCount", Start.AddMinutes(3), 6)
            .AddText(Finished + "/LastRunOutcome", Start.AddMinutes(3), "Succeeded");

        // Act
        var runs = await LoadAsync(store);

        // Assert
        Assert.Equal([new RunEntry(Start.AddMinutes(3), "Finished", AutomationRunOutcome.Succeeded, null)], runs.ToArray());
    }

    [Fact]
    public async Task WhenTheCountDropsToZero_ThenTheResetIsNoRunAndTheNextIncreaseIs()
    {
        // Arrange
        var store = CreateStore()
            .Add(Finished + "/RunCount", Start, 5)
            .Add(Finished + "/RunCount", Start.AddMinutes(1), 0)
            .Add(Finished + "/RunCount", Start.AddMinutes(2), 1);

        // Act
        var runs = await LoadAsync(store);

        // Assert
        Assert.Equal(Start.AddMinutes(2), Assert.Single(runs).At);
    }

    [Fact]
    public async Task WhenTheCountIsEqualToThePreviousOne_ThenItIsNoRun()
    {
        // Arrange
        var store = CreateStore()
            .Add(Finished + "/RunCount", Start, 5)
            .Add(Finished + "/RunCount", Start.AddMinutes(1), 5);

        // Act
        var runs = await LoadAsync(store);

        // Assert
        Assert.Empty(runs);
    }

    [Fact]
    public async Task WhenTheCountBeforeTheWindowIsKnown_ThenTheFirstSampleInTheWindowIsARun()
    {
        // Arrange
        var store = CreateStore()
            .Add(Finished + "/RunCount", Start.AddMinutes(-10), 3)
            .Add(Finished + "/RunCount", Start.AddMinutes(1), 4);

        // Act
        var runs = await LoadAsync(store);

        // Assert
        Assert.Equal(Start.AddMinutes(1), Assert.Single(runs).At);
    }

    [Fact]
    public async Task WhenRulesHaveMoreRunsThanTheLimit_ThenTheNewestRunsOfAllRulesAreListed()
    {
        // Arrange
        var store = CreateStore()
            .Add(Finished + "/RunCount", Start, 0)
            .Add(Finished + "/RunCount", Start.AddMinutes(1), 1)
            .Add(Finished + "/RunCount", Start.AddMinutes(4), 2)
            .Add(Started + "/RunCount", Start, 0)
            .Add(Started + "/RunCount", Start.AddMinutes(3), 1);

        // Act
        var runs = await LoadAsync(store, 2, new RunSource("Finished", Finished), new RunSource("Started", Started));

        // Assert
        Assert.Equal(
            [(Start.AddMinutes(4), "Finished"), (Start.AddMinutes(3), "Started")],
            runs.Select(run => (run.At!.Value, run.RuleName)).ToArray());
    }

    [Fact]
    public async Task WhenThereIsNoHistoryStore_ThenThereAreNoRuns()
    {
        // Act
        var runs = await RunHistory.LoadAsync([], [new RunSource("Finished", Finished)], Start, Start.AddMinutes(10), 10, CancellationToken.None);

        // Assert
        Assert.Empty(runs);
    }
}
