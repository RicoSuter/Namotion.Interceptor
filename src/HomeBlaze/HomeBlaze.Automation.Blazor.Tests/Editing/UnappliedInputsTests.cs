using HomeBlaze.Automation.Blazor.Editing;

namespace HomeBlaze.Automation.Blazor.Tests.Editing;

public class UnappliedInputsTests
{
    [Fact]
    public void WhenAFieldHasAPendingRow_ThenHasPendingRowIsTrueUntilItIsCleared()
    {
        // Arrange
        var pending = new UnappliedInputs();

        // Act
        var isMarked = pending.SetPendingRow("rule-1/trigger", true);
        var wasPending = pending.HasPendingRow;
        var isUnmarked = pending.SetPendingRow("rule-1/trigger", false);

        // Assert
        Assert.True(isMarked);
        Assert.True(wasPending);
        Assert.True(isUnmarked);
        Assert.False(pending.HasPendingRow);
    }

    [Fact]
    public void WhenTheSameStateIsSetAgain_ThenNothingChangesAndChangedIsRaisedOncePerChange()
    {
        // Arrange
        var pending = new UnappliedInputs();
        var raised = 0;
        pending.Changed += () => raised++;
        pending.SetPendingRow("a", true);

        // Act
        var marked = pending.SetPendingRow("a", true);
        var unmarked = pending.SetPendingRow("b", false);
        var isStillPending = pending.HasPendingRow;
        pending.SetPendingRow("a", false);
        var clearedAgain = pending.SetPendingRow("a", false);

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
        var pending = new UnappliedInputs();
        var tracker = new PendingRowTracker(pending, "rule-1/trigger/t1/condition");
        tracker.Report(true);

        // Act
        tracker.Dispose();

        // Assert
        Assert.False(pending.HasPendingRow);
    }

    [Fact]
    public void WhenATrackerWithAPendingRowIsDisposed_ThenTheDiscardedInputIsReported()
    {
        // Arrange
        var pending = new UnappliedInputs();
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
        var pending = new UnappliedInputs();
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
        Assert.False(pending.HasPendingRow);
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

    [Fact]
    public void WhenAnInputErrorIsSetAgainOrClearedTwice_ThenItIsListedUntilItIsClearedAndChangedIsRaisedOncePerChange()
    {
        // Arrange
        var inputs = new UnappliedInputs();
        var raised = 0;
        inputs.Changed += () => raised++;
        var error = new AutomationValidationError("finished", "For at least: use a duration such as 20s");

        // Act
        inputs.SetError("hold", error);
        inputs.SetError("hold", error);
        var withError = inputs.Errors.ToArray();
        inputs.SetError("hold", null);
        inputs.SetError("hold", null);

        // Assert
        Assert.Equal(error, Assert.Single(withError));
        Assert.Empty(inputs.Errors);
        Assert.Equal(2, raised);
    }
}
