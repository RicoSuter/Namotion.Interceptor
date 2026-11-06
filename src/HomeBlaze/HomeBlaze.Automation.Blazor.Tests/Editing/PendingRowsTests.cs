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
