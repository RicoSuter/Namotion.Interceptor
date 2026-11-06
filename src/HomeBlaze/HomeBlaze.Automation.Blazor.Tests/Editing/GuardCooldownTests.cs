using HomeBlaze.Automation.Blazor.Editing;

namespace HomeBlaze.Automation.Blazor.Tests.Editing;

public class GuardCooldownTests
{
    private const string Cooldown30 = "./LastTriggeredAt == null or now - ./LastTriggeredAt > 30m";

    [Theory]
    [InlineData(null, 30, Cooldown30)]
    [InlineData("not /Blinds/IsMoving", 30, "not /Blinds/IsMoving and (" + Cooldown30 + ")")]
    [InlineData("/a/B or /c/D", 30, "(/a/B or /c/D) and (" + Cooldown30 + ")")]
    [InlineData("/a/B", 0, "/a/B")]
    [InlineData(" ", 0, null)]
    public void WhenAConditionAndACooldownAreCombined_ThenTheGuardHasTheCooldownForm(string? condition, int minutes, string? expected)
    {
        // Act
        var guard = GuardCooldown.Combine(condition, minutes > 0 ? TimeSpan.FromMinutes(minutes) : null);

        // Assert
        Assert.Equal(expected, guard);
    }

    [Theory]
    [InlineData("not /Blinds/IsMoving")]
    [InlineData("/a/B or /c/D")]
    [InlineData("(/a/B) and /c/D")]
    [InlineData("/a/B ")]
    public void WhenACombinedGuardIsSplit_ThenTheConditionAndTheCooldownAreBack(string condition)
    {
        // Arrange
        var guard = GuardCooldown.Combine(condition, TimeSpan.FromMinutes(30));

        // Act
        var (splitCondition, cooldown) = GuardCooldown.Split(guard);

        // Assert
        Assert.Equal(condition, splitCondition);
        Assert.Equal(TimeSpan.FromMinutes(30), cooldown);
    }

    [Fact]
    public void WhenTheConditionIsWrappedInParentheses_ThenTheSplitDropsOnePair()
    {
        // Act
        var (wrapped, _) = GuardCooldown.Split(GuardCooldown.Combine("(/a/B or /c/D)", TimeSpan.FromMinutes(30)));
        var (handWritten, cooldown) = GuardCooldown.Split("(/a/B) and (" + Cooldown30 + ")");

        // Assert
        Assert.Equal("/a/B or /c/D", wrapped);
        Assert.Equal("/a/B", handWritten);
        Assert.Equal(TimeSpan.FromMinutes(30), cooldown);
    }

    [Fact]
    public void WhenTheGuardIsOnlyTheCooldown_ThenThereIsNoCondition()
    {
        // Act
        var (condition, cooldown) = GuardCooldown.Split(Cooldown30);

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
    public void WhenTheGuardHasAnotherForm_ThenItIsTheConditionWithoutACooldown(string guard)
    {
        // Act
        var (condition, cooldown) = GuardCooldown.Split(guard);

        // Assert
        Assert.Equal(guard, condition);
        Assert.Null(cooldown);
    }
}
