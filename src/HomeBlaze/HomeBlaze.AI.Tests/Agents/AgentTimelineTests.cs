using HomeBlaze.AI.Agents;
using Xunit;

namespace HomeBlaze.AI.Tests.Agents;

public class AgentTimelineTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Directory.CreateTempSubdirectory("agent-timeline").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static AgentTimelineEntry Entry(AgentTimelineEntryKind kind, int minute, string text = "", bool isQuiet = false) => new()
    {
        Kind = kind,
        Timestamp = Start.AddMinutes(minute),
        Text = text,
        IsQuiet = isQuiet
    };

    [Fact]
    public async Task WhenEntriesAreAppended_ThenTheyAreReloadedFromTheFile()
    {
        // Arrange
        var path = Path.Combine(_directory, "agent.timeline.jsonl");
        var timeline = new AgentTimeline(path);
        await timeline.AppendAsync(Entry(AgentTimelineEntryKind.User, 0, "hello"), CancellationToken.None);
        await timeline.AppendAsync(Entry(AgentTimelineEntryKind.Assistant, 1, "hi"), CancellationToken.None);

        // Act
        var reloaded = new AgentTimeline(path);
        await reloaded.LoadAsync(CancellationToken.None);

        // Assert
        Assert.Equal(["hello", "hi"], reloaded.Entries.Select(entry => entry.Text));
    }

    [Fact]
    public async Task WhenTheFileHasACorruptLine_ThenItIsSkipped()
    {
        // Arrange
        var path = Path.Combine(_directory, "agent.timeline.jsonl");
        var timeline = new AgentTimeline(path);
        await timeline.AppendAsync(Entry(AgentTimelineEntryKind.User, 0, "first"), CancellationToken.None);
        await File.AppendAllTextAsync(path, "{not json\n");
        await timeline.AppendAsync(Entry(AgentTimelineEntryKind.User, 1, "second"), CancellationToken.None);

        // Act
        var reloaded = new AgentTimeline(path);
        await reloaded.LoadAsync(CancellationToken.None);

        // Assert
        Assert.Equal(["first", "second"], reloaded.Entries.Select(entry => entry.Text));
    }

    [Fact]
    public async Task WhenLoadingMoreEntriesThanTheCapacity_ThenOnlyTheNewestAreKeptAndTheFileIsCompacted()
    {
        // Arrange
        var path = Path.Combine(_directory, "agent.timeline.jsonl");
        var timeline = new AgentTimeline(path, capacity: 10);
        for (var index = 0; index < 15; index++)
        {
            await timeline.AppendAsync(Entry(AgentTimelineEntryKind.User, index, index.ToString()), CancellationToken.None);
        }

        // Act
        var reloaded = new AgentTimeline(path, capacity: 10);
        await reloaded.LoadAsync(CancellationToken.None);

        // Assert
        Assert.Equal(10, reloaded.Entries.Count);
        Assert.Equal("5", reloaded.Entries[0].Text);
        Assert.Equal(10, File.ReadAllLines(path).Length);
    }

    [Fact]
    public async Task WhenAskingForRecentResults_ThenOnlyNonQuietBackgroundRunsAreReturnedNewestLast()
    {
        // Arrange
        var timeline = new AgentTimeline(filePath: null);
        await timeline.AppendAsync(Entry(AgentTimelineEntryKind.BackgroundRun, 0, "one"), CancellationToken.None);
        await timeline.AppendAsync(Entry(AgentTimelineEntryKind.BackgroundRun, 1, "[quiet] nothing", isQuiet: true), CancellationToken.None);
        await timeline.AppendAsync(Entry(AgentTimelineEntryKind.BackgroundRun, 2, "two"), CancellationToken.None);
        await timeline.AppendAsync(Entry(AgentTimelineEntryKind.BackgroundRun, 3, "three"), CancellationToken.None);

        // Act
        var recent = timeline.GetRecentBackgroundResults(2);
        var lastQuiet = timeline.GetLastQuietRunTime();

        // Assert
        Assert.Equal(["two", "three"], recent.Select(entry => entry.Text));
        Assert.Equal(Start.AddMinutes(1), lastQuiet);
    }

    [Fact]
    public async Task WhenAskingForFindingsSinceTheLastReply_ThenOnlyLaterNonQuietRunsAreReturned()
    {
        // Arrange
        var timeline = new AgentTimeline(filePath: null);
        await timeline.AppendAsync(Entry(AgentTimelineEntryKind.BackgroundRun, 0, "old"), CancellationToken.None);
        await timeline.AppendAsync(Entry(AgentTimelineEntryKind.Assistant, 1, "reply"), CancellationToken.None);
        await timeline.AppendAsync(Entry(AgentTimelineEntryKind.BackgroundRun, 2, "[quiet] calm", isQuiet: true), CancellationToken.None);
        await timeline.AppendAsync(Entry(AgentTimelineEntryKind.BackgroundRun, 3, "new finding"), CancellationToken.None);

        // Act
        var findings = timeline.GetFindingsSinceLastReply();

        // Assert
        Assert.Equal(["new finding"], findings.Select(entry => entry.Text));
    }
}
