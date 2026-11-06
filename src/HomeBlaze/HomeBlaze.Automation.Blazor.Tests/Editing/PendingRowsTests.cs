using HomeBlaze.Automation.Blazor.Editing;

namespace HomeBlaze.Automation.Blazor.Tests.Editing;

public class PendingRowsTests
{
    [Fact]
    public void WhenAFieldHasAPendingRow_ThenAnyIsTrueUntilItIsCleared()
    {
        // Arrange
        var pending = new PendingRows();

        // Act
        var isMarked = pending.Set("rule-1/trigger", true);
        var wasPending = pending.Any;
        var isUnmarked = pending.Set("rule-1/trigger", false);

        // Assert
        Assert.True(isMarked);
        Assert.True(wasPending);
        Assert.True(isUnmarked);
        Assert.False(pending.Any);
    }

    [Fact]
    public void WhenTheSameStateIsSetAgain_ThenNothingChanges()
    {
        // Arrange
        var pending = new PendingRows();
        pending.Set("a", true);

        // Act
        var marked = pending.Set("a", true);
        var unmarked = pending.Set("b", false);

        // Assert
        Assert.False(marked);
        Assert.False(unmarked);
        Assert.True(pending.Any);
    }

    [Fact]
    public void WhenAPrefixIsCleared_ThenOnlyTheFieldsOfThatPrefixAreForgotten()
    {
        // Arrange
        var pending = new PendingRows();
        pending.Set("rule-1/trigger", true);
        pending.Set("rule-1/guard", true);
        pending.Set("rule-2/trigger", true);

        // Act
        var cleared = pending.Clear("rule-1/");

        // Assert
        Assert.True(cleared);
        Assert.True(pending.Any);
        Assert.True(pending.Clear("rule-2/"));
        Assert.False(pending.Any);
    }
}

public class PendingRowsLifecycleTests
{
    [Fact]
    public void WhenAFieldIsMarkedOrCleared_ThenChangedIsRaisedOncePerChange()
    {
        // Arrange
        var pending = new PendingRows();
        var raised = 0;
        pending.Changed += () => raised++;

        // Act
        pending.Set("a", true);
        pending.Set("a", true);
        pending.Set("a", false);
        pending.Set("a", false);

        // Assert
        Assert.Equal(2, raised);
    }

    [Fact]
    public void WhenARuleIsRemoved_ThenClearingItsPrefixForgetsItsFieldsAndNotThoseOfARuleWithTheSameStart()
    {
        // Arrange
        var pending = new PendingRows();
        pending.Set(PendingRowIds.For("rule-1", "trigger", "t1", "condition"), true);
        pending.Set(PendingRowIds.For("rule-1", "guard", "g1", "condition"), true);
        pending.Set(PendingRowIds.For("rule-10", "trigger", "t1", "condition"), true);
        var raised = 0;
        pending.Changed += () => raised++;

        // Act
        pending.Clear(PendingRowIds.ForRule("rule-1"));

        // Assert
        Assert.True(pending.Any);
        Assert.Equal(1, raised);
        pending.Clear(PendingRowIds.ForRule("rule-10"));
        Assert.False(pending.Any);
    }

    [Fact]
    public void WhenItemsAreReordered_ThenTheirFieldIdsStayTheSame()
    {
        // Arrange
        var first = new DraftItem<string>("first");
        var second = new DraftItem<string>("second");
        var items = new List<DraftItem<string>> { first, second };
        var before = items.Select(item => PendingRowIds.For("rule-1", "trigger", item.Id, "condition")).ToList();

        // Act
        DraftLists.Move(items, second, -1);
        var after = items.Select(item => PendingRowIds.For("rule-1", "trigger", item.Id, "condition")).ToList();

        // Assert
        Assert.Equal(first, items[1]);
        Assert.Equal(before[0], after[1]);
        Assert.Equal(before[1], after[0]);
        Assert.NotEqual(before[0], before[1]);
    }

    [Fact]
    public void WhenTheTrackerIsDisposed_ThenItsFieldIsCleared()
    {
        // Arrange
        var pending = new PendingRows();
        var tracker = new PendingRowTracker(pending, "rule-1/trigger/t1/condition");
        tracker.Report(true);

        // Act
        tracker.Dispose();

        // Assert
        Assert.False(pending.Any);
    }

    [Fact]
    public void WhenTheTrackerHasNoRegistry_ThenReportingDoesNothing()
    {
        // Arrange
        var tracker = new PendingRowTracker(null, "a");

        // Act
        tracker.Report(true);
        tracker.Dispose();

        // Assert
        Assert.NotNull(tracker);
    }
}
