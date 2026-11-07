using HomeBlaze.Automation.Persistence;

namespace HomeBlaze.Automation.Tests.Persistence;

public sealed class AutomationStateKeeperTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Directory.CreateTempSubdirectory("automation-keeper").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void WhenAWriteFails_ThenThePersistedCheckpointStays()
    {
        // Arrange
        var path = Path.Combine(_directory, "Storm.json");
        var keeper = new AutomationStateKeeper(new AutomationCheckpointStore(path));
        Assert.Null(keeper.TryPersist(new AutomationCheckpoint { CurrentState = "Idle" }));
        File.Delete(path);
        Directory.CreateDirectory(path);

        // Act
        var error = keeper.TryPersist(AutomationStateKeeper.WithState(keeper.Current, "Running", Now));

        // Assert
        Assert.NotNull(error);
        Assert.Equal("Idle", keeper.Current.CurrentState);
    }

    [Fact]
    public void WhenTheFileIsNoCheckpoint_ThenLoadingReturnsTheError()
    {
        // Arrange
        var path = Path.Combine(_directory, "Storm.json");
        File.WriteAllText(path, "not a checkpoint");
        var keeper = new AutomationStateKeeper(new AutomationCheckpointStore(path));

        // Act
        var checkpoint = keeper.Load(Now, out var error);

        // Assert
        Assert.Null(checkpoint);
        Assert.NotNull(error);
    }

    [Fact]
    public void WhenAnUnreadableCheckpointCannotBeMovedAside_ThenNothingIsWrittenUntilOverwritingIsAllowed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Arrange
        var folder = Directory.CreateDirectory(Path.Combine(_directory, "Locked")).FullName;
        var path = Path.Combine(folder, "Storm.json");
        File.WriteAllText(path, "not a checkpoint");
        var keeper = new AutomationStateKeeper(new AutomationCheckpointStore(path));
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            keeper.Load(Now, out _);
        }
        finally
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // Act
        var writeError = keeper.TryPersist(new AutomationCheckpoint { CurrentState = "Idle" });
        var content = File.ReadAllText(path);
        keeper.AllowOverwrite();
        var errorAfterAllowing = keeper.TryPersist(new AutomationCheckpoint { CurrentState = "Idle" });

        // Assert
        Assert.NotNull(writeError);
        Assert.Equal("not a checkpoint", content);
        Assert.Null(errorAfterAllowing);
        Assert.Contains("Idle", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("""{ "currentState": "Idle", "rules": null }""")]
    [InlineData("""{ "currentState": "Idle", "rules": { "retract": null } }""")]
    public void WhenTheCheckpointHoldsNullRules_ThenItLoadsWithoutRuns(string json)
    {
        // Arrange
        var path = Path.Combine(_directory, "Storm.json");
        File.WriteAllText(path, json);
        var keeper = new AutomationStateKeeper(new AutomationCheckpointStore(path));
        var rule = new AutomationRule();

        // Act
        var checkpoint = keeper.Load(Now, out var error);
        keeper.RestoreAllRules(new Dictionary<string, AutomationRule> { ["retract"] = rule });

        // Assert
        Assert.Null(error);
        Assert.NotNull(checkpoint);
        Assert.Equal("Idle", checkpoint.CurrentState);
        Assert.Empty(keeper.Current.Rules);
        Assert.Equal(0L, rule.RunCount);
    }

    [Fact]
    public void WhenRulesAreRestored_ThenOnlyRulesWithoutARunCountGetThePersistedRuns()
    {
        // Arrange
        var keeper = new AutomationStateKeeper(new AutomationCheckpointStore(null));
        var run = new RuleCheckpoint { RunCount = 4, LastTriggeredAt = Now, LastRunOutcome = AutomationRunOutcome.Succeeded, LastRunMessage = "Ran /Blinds/Close()" };
        keeper.TryPersist(AutomationStateKeeper.WithRule(new AutomationCheckpoint(), "retract", run));
        var restored = new AutomationRule();
        var added = new AutomationRule();
        var running = new AutomationRule { RunCount = 7 };

        // Act
        keeper.RestoreRules(new Dictionary<string, AutomationRule> { ["retract"] = restored, ["added"] = added, ["running"] = running });

        // Assert
        Assert.Equal(4L, restored.RunCount);
        Assert.Equal(Now, restored.LastTriggeredAt);
        Assert.Equal(AutomationRunOutcome.Succeeded, restored.LastRunOutcome);
        Assert.Equal("Ran /Blinds/Close()", restored.LastRunMessage);
        Assert.Equal(0L, added.RunCount);
        Assert.Equal(7L, running.RunCount);
    }

    [Fact]
    public void WhenTriggerTimesAreClearedOrRulesRemoved_ThenTheOtherRunPropertiesStay()
    {
        // Arrange
        var checkpoint = AutomationStateKeeper.WithRule(
            AutomationStateKeeper.WithRule(new AutomationCheckpoint(), "a", new RuleCheckpoint { RunCount = 2, LastTriggeredAt = Now }),
            "b",
            new RuleCheckpoint { RunCount = 5, LastTriggeredAt = Now });

        // Act
        var cleared = AutomationStateKeeper.WithoutTriggerTimes(checkpoint);
        var pruned = AutomationStateKeeper.WithRulesOf(checkpoint, ["b"]);

        // Assert
        Assert.Null(cleared.Rules["a"].LastTriggeredAt);
        Assert.Equal(2L, cleared.Rules["a"].RunCount);
        Assert.Equal(["b"], pruned.Rules.Keys.ToArray());
    }
}
