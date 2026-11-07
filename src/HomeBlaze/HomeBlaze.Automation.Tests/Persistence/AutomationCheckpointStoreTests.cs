using HomeBlaze.Automation.Persistence;

namespace HomeBlaze.Automation.Tests.Persistence;

public sealed class AutomationCheckpointStoreTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("automation-checkpoint").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData("/Automations/Storm", "AutomationState/Automations/Storm.json")]
    [InlineData("/Pages/Home.md/Storm", "AutomationState/Pages/Home.md/Storm.json")]
    [InlineData("/Laundry/Washer_2", "AutomationState/Laundry/Washer_2.json")]
    [InlineData("/A:B/C?", "AutomationState/A%3AB/C%3F.json")]
    [InlineData("/100%", "AutomationState/100%25.json")]
    [InlineData("/a/..", "AutomationState/a/%2E%2E.json")]
    [InlineData("/Dot./Space ", "AutomationState/Dot%2E/Space%20.json")]
    [InlineData("/CON/lpt1.x", "AutomationState/%43ON/%6Cpt1.x.json")]
    [InlineData("/Storm.json/Rain", "AutomationState/Storm%2Ejson/Rain.json")]
    [InlineData("/", "AutomationState/%2F.json")]
    public void WhenAnAutomationPathIsMapped_ThenTheFileMirrorsIt(string automationPath, string expected)
    {
        // Act
        var path = AutomationCheckpointStore.GetFilePath(_directory, automationPath);

        // Assert
        Assert.Equal(Path.Combine(_directory, expected.Replace('/', Path.DirectorySeparatorChar)), path);
    }

    [Theory]
    [InlineData("/a:b", "/a_b")]
    [InlineData("/x/a?b", "/x/a*b")]
    [InlineData("/a/..", "/a/_")]
    [InlineData("/a%3Ab", "/a:b")]
    [InlineData("/Storm.json/Rain", "/Storm")]
    [InlineData("/", "/_root")]
    public void WhenTwoAutomationPathsDiffer_ThenTheirFilesDifferAndNeitherIsInTheFolderOfTheOther(string first, string second)
    {
        // Act
        var firstPath = AutomationCheckpointStore.GetFilePath(_directory, first)!;
        var secondPath = AutomationCheckpointStore.GetFilePath(_directory, second)!;

        // Assert
        Assert.NotEqual(firstPath, secondPath, StringComparer.OrdinalIgnoreCase);
        Assert.False(firstPath.StartsWith(secondPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Assert.False(secondPath.StartsWith(firstPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WhenThereIsNoDataDirectoryOrPath_ThenThereIsNoFile()
    {
        // Act
        var withoutDirectory = AutomationCheckpointStore.GetFilePath(null, "/Storm");
        var withoutPath = AutomationCheckpointStore.GetFilePath(_directory, null);

        // Assert
        Assert.Null(withoutDirectory);
        Assert.Null(withoutPath);
    }

    [Fact]
    public void WhenACheckpointIsWritten_ThenItIsReadBackAndNoTemporaryFileRemains()
    {
        // Arrange
        var store = new AutomationCheckpointStore(Path.Combine(_directory, "AutomationState", "Storm.json"));
        var run = new RuleCheckpoint { RunCount = 3, LastRunOutcome = AutomationRunOutcome.DryRun, LastRunMessage = "Would run /Blinds/Close()" };
        var checkpoint = new AutomationCheckpoint
        {
            CurrentState = "Open",
            StateEnteredAt = new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero),
            Rules = new Dictionary<string, RuleCheckpoint> { ["remind"] = run }
        };

        // Act
        store.Write(checkpoint);
        var read = store.Read();

        // Assert
        Assert.NotNull(read);
        Assert.Equal("Open", read.CurrentState);
        Assert.Equal(checkpoint.StateEnteredAt, read.StateEnteredAt);
        Assert.Equal(run, read.Rules["remind"]);
        Assert.Equal([store.FilePath!], Directory.GetFiles(Path.Combine(_directory, "AutomationState")));
        Assert.Contains("\"lastRunOutcome\": \"DryRun\"", File.ReadAllText(store.FilePath!));
    }

    [Fact]
    public void WhenTheFileHoldsARunOutcomeThatNoLongerExists_ThenTheOutcomeIsReadAsNoneAndTheRestIsKept()
    {
        // Arrange
        var path = Path.Combine(_directory, "AutomationState", "Storm.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            {
              "currentState": "Open",
              "rules": {
                "retract": { "runCount": 3, "lastRunOutcome": "rejectedByGuard", "lastRunMessage": "Only if is false" },
                "close": { "runCount": 1, "lastRunOutcome": "succeeded" }
              }
            }
            """);

        // Act
        var checkpoint = new AutomationCheckpointStore(path).Read();

        // Assert
        Assert.Equal("Open", checkpoint?.CurrentState);
        Assert.Equal(3, checkpoint?.Rules["retract"].RunCount);
        Assert.Null(checkpoint?.Rules["retract"].LastRunOutcome);
        Assert.Equal(AutomationRunOutcome.Succeeded, checkpoint?.Rules["close"].LastRunOutcome);
    }

    [Fact]
    public void WhenThereIsNoFile_ThenNothingIsRead()
    {
        // Act
        var withoutFile = new AutomationCheckpointStore(Path.Combine(_directory, "Missing.json")).Read();
        var withoutPath = new AutomationCheckpointStore(null).Read();

        // Assert
        Assert.Null(withoutFile);
        Assert.Null(withoutPath);
    }

    [Fact]
    public void WhenTheStoreIsDeleted_ThenTheFileIsGoneAndLaterWritesDoNothing()
    {
        // Arrange
        var path = Path.Combine(_directory, "AutomationState", "Storm.json");
        var store = new AutomationCheckpointStore(path);
        store.Write(new AutomationCheckpoint());

        // Act
        store.Delete();
        store.Write(new AutomationCheckpoint { CurrentState = "Open" });

        // Assert
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void WhenAnotherStoreTakesTheFile_ThenTheFirstStoreNoLongerWritesOrDeletesIt()
    {
        // Arrange
        var path = Path.Combine(_directory, "AutomationState", "Storm.json");
        var first = new AutomationCheckpointStore(path);
        first.Write(new AutomationCheckpoint { CurrentState = "Open" });

        // Act
        var second = new AutomationCheckpointStore(path);
        second.Write(new AutomationCheckpoint { CurrentState = "Closed" });
        first.Write(new AutomationCheckpoint { CurrentState = "Open" });
        var isDeletedByTheFirst = first.Delete();

        // Assert
        Assert.False(isDeletedByTheFirst);
        Assert.Equal("Closed", second.Read()?.CurrentState);
    }

    [Fact]
    public void WhenAStoreIsReleased_ThenItNoLongerWritesTheFile()
    {
        // Arrange
        var path = Path.Combine(_directory, "AutomationState", "Storm.json");
        var store = new AutomationCheckpointStore(path);
        store.Write(new AutomationCheckpoint { CurrentState = "Open" });

        // Act
        store.Release();
        store.Write(new AutomationCheckpoint { CurrentState = "Closed" });

        // Assert
        Assert.Equal("Open", store.Read()?.CurrentState);
    }

    [Fact]
    public void WhenTheFileCannotBeReplaced_ThenTheWriteThrows()
    {
        // Arrange
        var path = Path.Combine(_directory, "AutomationState", "Storm.json");
        Directory.CreateDirectory(path);
        var store = new AutomationCheckpointStore(path);

        // Act
        var exception = Record.Exception(() => store.Write(new AutomationCheckpoint()));

        // Assert
        Assert.True(exception is IOException or UnauthorizedAccessException, exception?.ToString());
    }
}
