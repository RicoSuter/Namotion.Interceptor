using HomeBlaze.Automation.Persistence;

namespace HomeBlaze.Automation.Tests.Persistence;

public sealed class AutomationCheckpointStoreTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("automation-checkpoint").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData("/Automations/Storm", "Automations/Automations/Storm.json")]
    [InlineData("/Pages/Home.md/Storm", "Automations/Pages/Home.md/Storm.json")]
    [InlineData("/A:B/C?", "Automations/A_B/C_.json")]
    [InlineData("/", "Automations/_root.json")]
    public void WhenAnAutomationPathIsMapped_ThenTheFileMirrorsIt(string automationPath, string expected)
    {
        // Act
        var path = AutomationCheckpointStore.GetFilePath(_directory, automationPath);

        // Assert
        Assert.Equal(Path.Combine(_directory, expected.Replace('/', Path.DirectorySeparatorChar)), path);
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
        var store = new AutomationCheckpointStore(Path.Combine(_directory, "Automations", "Storm.json"));
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
        Assert.Equal([store.FilePath!], Directory.GetFiles(Path.Combine(_directory, "Automations")));
        Assert.Contains("\"lastRunOutcome\": \"DryRun\"", File.ReadAllText(store.FilePath!));
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
        var path = Path.Combine(_directory, "Automations", "Storm.json");
        var store = new AutomationCheckpointStore(path);
        store.Write(new AutomationCheckpoint());

        // Act
        store.Delete();
        store.Write(new AutomationCheckpoint { CurrentState = "Open" });

        // Assert
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void WhenTheFileCannotBeReplaced_ThenTheWriteThrows()
    {
        // Arrange
        var path = Path.Combine(_directory, "Automations", "Storm.json");
        Directory.CreateDirectory(path);
        var store = new AutomationCheckpointStore(path);

        // Act
        var exception = Record.Exception(() => store.Write(new AutomationCheckpoint()));

        // Assert
        Assert.True(exception is IOException or UnauthorizedAccessException, exception?.ToString());
    }
}
