using HomeBlaze.Game.View;

namespace HomeBlaze.Game.Tests.View;

public class GameViewPartTests
{
    [Fact]
    public void WhenAnimationModeIsOnce_ThenViewPartCarriesOne()
    {
        // Arrange
        var visual = new GameVisual([new GameVisualPart { Shape = GameVisualShape.Model, Animation = "Open", AnimationMode = GameVisualAnimationMode.Once }]);

        // Act
        var parts = GameViewPart.FromVisual(visual);

        // Assert
        Assert.Equal(1, Assert.Single(parts).AnimationMode);
    }

    [Fact]
    public void WhenAnimationModeIsNotSet_ThenViewPartCarriesZero()
    {
        // Arrange
        var visual = new GameVisual([new GameVisualPart { Shape = GameVisualShape.Model, Animation = "Walk" }]);

        // Act
        var parts = GameViewPart.FromVisual(visual);

        // Assert
        Assert.Equal(0, Assert.Single(parts).AnimationMode);
    }
}
