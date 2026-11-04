using HomeBlaze.AI.Agents;
using HomeBlaze.AI.Blazor;
using Xunit;

namespace HomeBlaze.AI.Tests.Blazor;

public class AgentTimelineRowsTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WhenTheTimelineHasFewerRowsThanTheLimit_ThenAllRowsAreReturnedOldestFirst()
    {
        // Arrange
        var entries = CreateEntries("U", "A", "U", "A");
        var rows = new List<AgentTimelineRow>();

        // Act
        var hasEarlierRows = AgentTimelineRows.GetNewest(entries, 50, rows);

        // Assert
        Assert.False(hasEarlierRows);
        Assert.Equal([0, 1, 2, 3], rows.Select(row => row.LastIndex));
        Assert.All(rows, row => Assert.Equal(1, row.EntryCount));
    }

    [Fact]
    public void WhenTheTimelineHasMoreRowsThanTheLimit_ThenTheNewestRowsAreReturned()
    {
        // Arrange
        var entries = CreateEntries(Enumerable.Repeat("U", 120).ToArray());
        var rows = new List<AgentTimelineRow>();

        // Act
        var hasEarlierRows = AgentTimelineRows.GetNewest(entries, AgentTimelineRows.PageSize, rows);

        // Assert
        Assert.True(hasEarlierRows);
        Assert.Equal(50, rows.Count);
        Assert.Equal(70, rows[0].FirstIndex);
        Assert.Equal(119, rows[^1].LastIndex);
    }

    [Fact]
    public void WhenShowingEarlierRows_ThenEachStepAddsAPageUntilAllRowsAreShown()
    {
        // Arrange
        var entries = CreateEntries(Enumerable.Repeat("A", 120).ToArray());
        var rows = new List<AgentTimelineRow>();

        // Act
        var afterOne = AgentTimelineRows.GetNewest(entries, AgentTimelineRows.PageSize * 2, rows);
        var countAfterOne = rows.Count;
        var afterTwo = AgentTimelineRows.GetNewest(entries, AgentTimelineRows.PageSize * 3, rows);

        // Assert
        Assert.True(afterOne);
        Assert.Equal(100, countAfterOne);
        Assert.False(afterTwo);
        Assert.Equal(120, rows.Count);
        Assert.Equal(0, rows[0].FirstIndex);
    }

    [Fact]
    public void WhenQuietRunsFollowEachOther_ThenTheyFormOneRowShowingTheNewest()
    {
        // Arrange
        var entries = CreateEntries("U", "Q", "Q", "Q", "A", "Q");
        var rows = new List<AgentTimelineRow>();

        // Act
        AgentTimelineRows.GetNewest(entries, 50, rows);

        // Assert
        Assert.Equal([new(0, 0), new(1, 3), new(4, 4), new(5, 5)], rows);
        Assert.Equal(3, rows[1].EntryCount);
    }

    [Fact]
    public void WhenTheLimitEndsInsideAQuietGroup_ThenTheGroupIsKeptWhole()
    {
        // Arrange
        var entries = CreateEntries("U", "Q", "Q", "Q", "Q", "A", "D", "U");
        var rows = new List<AgentTimelineRow>();

        // Act
        var hasEarlierRows = AgentTimelineRows.GetNewest(entries, 4, rows);

        // Assert
        Assert.True(hasEarlierRows);
        Assert.Equal([new(1, 4), new(5, 5), new(6, 6), new(7, 7)], rows);
    }

    private static List<AgentTimelineEntry> CreateEntries(params string[] kinds) =>
        kinds.Select((kind, index) => kind switch
        {
            "U" => new AgentTimelineEntry { Kind = AgentTimelineEntryKind.User, Timestamp = Start.AddMinutes(index) },
            "A" => new AgentTimelineEntry { Kind = AgentTimelineEntryKind.Assistant, Timestamp = Start.AddMinutes(index) },
            "Q" => new AgentTimelineEntry { Kind = AgentTimelineEntryKind.BackgroundRun, IsQuiet = true, Timestamp = Start.AddMinutes(index) },
            "D" => new AgentTimelineEntry { Kind = AgentTimelineEntryKind.Divider, Timestamp = Start.AddMinutes(index) },
            _ => throw new ArgumentException($"Unknown entry kind '{kind}'.")
        }).ToList();
}
