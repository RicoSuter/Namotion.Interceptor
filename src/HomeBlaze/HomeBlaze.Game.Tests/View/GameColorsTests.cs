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
}
