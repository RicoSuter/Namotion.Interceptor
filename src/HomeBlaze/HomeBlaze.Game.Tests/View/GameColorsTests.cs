using HomeBlaze.Game.View;

namespace HomeBlaze.Game.Tests.View;

public class GameColorsTests
{
    [Fact]
    public void WhenTheSameIdIsColoredTwice_ThenTheColorIsTheSame()
    {
        // Act
        var first = GameColors.FromId("kitchen");
        var second = GameColors.FromId("kitchen");

        // Assert
        Assert.Equal(first, second);
        Assert.Matches("^#[0-9a-f]{6}$", first);
    }

    [Fact]
    public void WhenDifferentIdsAreColored_ThenTheColorsDiffer()
    {
        // Act
        var kitchen = GameColors.FromId("kitchen");
        var office = GameColors.FromId("office");

        // Assert
        Assert.NotEqual(kitchen, office);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("red")]
    [InlineData("#12345")]
    [InlineData("#1234567")]
    [InlineData("#12345g")]
    public void WhenTheStoredColorIsNotAHexColor_ThenTheColorOfTheIdIsUsed(string? stored)
    {
        // Act
        var resolved = GameColors.Resolve(stored, "tab-1");

        // Assert
        Assert.Equal(GameColors.FromId("tab-1"), resolved);
    }

    [Fact]
    public void WhenTheStoredColorIsAHexColor_ThenItIsUsedInLowerCase()
    {
        // Act
        var resolved = GameColors.Resolve("#A1B2C3", "tab-1");

        // Assert
        Assert.Equal("#a1b2c3", resolved);
    }
}
