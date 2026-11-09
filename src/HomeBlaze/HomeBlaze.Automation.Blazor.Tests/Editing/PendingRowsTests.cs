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
    public void WhenTheSameStateIsSetAgain_ThenNothingChangesAndChangedIsRaisedOncePerChange()
    {
        // Arrange
        var pending = new PendingRows();
        var raised = 0;
        pending.Changed += () => raised++;
        pending.Set("a", true);

        // Act
        var marked = pending.Set("a", true);
        var unmarked = pending.Set("b", false);
        var isStillPending = pending.Any;
        pending.Set("a", false);
        var clearedAgain = pending.Set("a", false);

        // Assert
        Assert.False(marked);
        Assert.False(unmarked);
        Assert.True(isStillPending);
        Assert.False(clearedAgain);
        Assert.Equal(2, raised);
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
    public void WhenATrackerWithAPendingRowIsDisposed_ThenTheDiscardedInputIsReported()
    {
        // Arrange
        var pending = new PendingRows();
        var discarded = new List<string>();
        pending.Discarded += discarded.Add;
        var finished = new PendingRowTracker(pending, "a");
        var unfinished = new PendingRowTracker(pending, "b");
        unfinished.Report(true);

        // Act
        finished.Dispose();
        unfinished.Dispose();

        // Assert
        Assert.Equal(["An unfinished condition"], discarded.ToArray());
    }

    [Fact]
    public void WhenTheFieldDropsAnEditWhileItStays_ThenTheDiscardedInputIsReported()
    {
        // Arrange
        var pending = new PendingRows();
        var discarded = new List<string>();
        pending.Discarded += discarded.Add;
        var tracker = new PendingRowTracker(pending, "a");
        tracker.Report(true);

        // Act
        tracker.Discard();
        tracker.Report(false);
        tracker.Dispose();

        // Assert
        Assert.Equal(["An unfinished condition"], discarded.ToArray());
        Assert.False(pending.Any);
    }

    [Fact]
    public void WhenTheTrackerHasNoRegistry_ThenReportingDoesNothing()
    {
        // Arrange
        var tracker = new PendingRowTracker(null, "a");

        // Act
        var exception = Record.Exception(() =>
        {
            tracker.Report(true);
            tracker.Discard();
            tracker.Dispose();
        });

        // Assert
        Assert.Null(exception);
    }
}
