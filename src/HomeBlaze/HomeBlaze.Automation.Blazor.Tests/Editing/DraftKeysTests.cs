using HomeBlaze.Automation.Blazor.Editing;

namespace HomeBlaze.Automation.Blazor.Tests.Editing;

public class DraftKeysTests
{
    [Fact]
    public void WhenARuleKeyIsCreated_ThenItIsShortAndNew()
    {
        // Arrange
        string[] existing = ["abcd"];

        // Act
        var key = DraftKeys.NewRuleKey(existing, new Random(1));

        // Assert
        Assert.Matches("^[a-z][a-z0-9]{3}$", key);
        Assert.DoesNotContain(key, existing);
    }

    [Fact]
    public void WhenTheFirstKeyIsTaken_ThenAnotherOneIsCreated()
    {
        // Arrange
        var taken = DraftKeys.NewRuleKey([], new Random(7));

        // Act
        var key = DraftKeys.NewRuleKey([taken], new Random(7));

        // Assert
        Assert.NotEqual(taken, key);
    }

    [Fact]
    public void WhenAnActionIdIsCreated_ThenItHasTheFirstFreeNumber()
    {
        // Act
        var id = DraftKeys.NewActionId("call", ["call-1", "call-3"]);

        // Assert
        Assert.Equal("call-2", id);
    }
}
