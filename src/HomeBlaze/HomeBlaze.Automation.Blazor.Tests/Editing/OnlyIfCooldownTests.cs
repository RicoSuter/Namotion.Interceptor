using HomeBlaze.Automation.Blazor.Editing;

namespace HomeBlaze.Automation.Blazor.Tests.Editing;

public class OnlyIfCooldownTests
{
    private const string Cooldown30 = "./LastTriggeredAt == null or now - ./LastTriggeredAt > 30m";

    [Theory]
    [InlineData(null, 30, Cooldown30)]
    [InlineData("not /Blinds/IsMoving", 30, "not /Blinds/IsMoving and (" + Cooldown30 + ")")]
    [InlineData("/a/B or /c/D", 30, "(/a/B or /c/D) and (" + Cooldown30 + ")")]
    [InlineData("/a/B", 0, "/a/B")]
    [InlineData(" ", 0, null)]
    public void WhenAConditionAndACooldownAreCombined_ThenOnlyIfHasTheCooldownForm(string? condition, int minutes, string? expected)
    {
        // Act
        var onlyIf = OnlyIfCooldown.Combine(condition, minutes > 0 ? TimeSpan.FromMinutes(minutes) : null);

        // Assert
        Assert.Equal(expected, onlyIf);
    }

    [Theory]
    [InlineData("/Rooms[Bob's room]/Temperature > 1 or /c/D", "(/Rooms[Bob's room]/Temperature > 1 or /c/D) and (" + Cooldown30 + ")")]
    [InlineData("/Rooms[a or b]/Temperature > 1", "/Rooms[a or b]/Temperature > 1 and (" + Cooldown30 + ")")]
    [InlineData("/Rooms[(]/Temperature > 1 or /c/D", "(/Rooms[(]/Temperature > 1 or /c/D) and (" + Cooldown30 + ")")]
    public void WhenAConditionHasAKeyInBrackets_ThenItsQuotesWordsAndParenthesesAreReadAsPartOfThePath(string condition, string expected)
    {
        // Act
        var onlyIf = OnlyIfCooldown.Combine(condition, TimeSpan.FromMinutes(30));
        var (splitCondition, cooldown) = OnlyIfCooldown.Split(onlyIf);

        // Assert
        Assert.Equal(expected, onlyIf);
        Assert.Equal(condition, splitCondition);
        Assert.Equal(TimeSpan.FromMinutes(30), cooldown);
    }

    [Theory]
    [InlineData("not /Blinds/IsMoving")]
    [InlineData("/a/B or /c/D")]
    [InlineData("(/a/B) and /c/D")]
    [InlineData("/a/B ")]
    public void WhenACombinedOnlyIfIsSplit_ThenTheConditionAndTheCooldownAreBack(string condition)
    {
        // Arrange
        var onlyIf = OnlyIfCooldown.Combine(condition, TimeSpan.FromMinutes(30));

        // Act
        var (splitCondition, cooldown) = OnlyIfCooldown.Split(onlyIf);

        // Assert
        Assert.Equal(condition, splitCondition);
        Assert.Equal(TimeSpan.FromMinutes(30), cooldown);
    }

    [Fact]
    public void WhenTheConditionIsWrappedInParentheses_ThenTheSplitDropsOnePair()
    {
        // Act
        var (wrapped, _) = OnlyIfCooldown.Split(OnlyIfCooldown.Combine("(/a/B or /c/D)", TimeSpan.FromMinutes(30)));
        var (handWritten, cooldown) = OnlyIfCooldown.Split("(/a/B) and (" + Cooldown30 + ")");

        // Assert
        Assert.Equal("/a/B or /c/D", wrapped);
        Assert.Equal("/a/B", handWritten);
        Assert.Equal(TimeSpan.FromMinutes(30), cooldown);
    }

    [Fact]
    public void WhenOnlyIfIsOnlyTheCooldown_ThenThereIsNoCondition()
    {
        // Act
        var (condition, cooldown) = OnlyIfCooldown.Split(Cooldown30);

        // Assert
        Assert.Null(condition);
        Assert.Equal(TimeSpan.FromMinutes(30), cooldown);
    }

    [Theory]
    [InlineData("now - ./LastTriggeredAt > 30m")]
    [InlineData("/a/B and (./LastTriggeredAt == null or now - ./LastTriggeredAt > 0s)")]
    [InlineData("(/a/B and (" + Cooldown30 + "))")]
    [InlineData("/a/B) and (" + Cooldown30 + ")")]
    [InlineData("/a/B or /c/D and (" + Cooldown30 + ")")]
    [InlineData("/a/B || /c/D && (" + Cooldown30 + ")")]
    public void WhenOnlyIfHasAnotherForm_ThenItIsTheConditionWithoutACooldown(string onlyIf)
    {
        // Act
        var (condition, cooldown) = OnlyIfCooldown.Split(onlyIf);

        // Assert
        Assert.Equal(onlyIf, condition);
        Assert.Null(cooldown);
    }
}
