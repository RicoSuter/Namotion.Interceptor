using System.Collections.Immutable;

namespace HomeBlaze.Game.Tests.Visuals;

public class GameVisualTests
{
    [Fact]
    public void WhenTwoVisualsHaveEqualParts_ThenTheyAreEqual()
    {
        // Arrange
        var first = new GameVisual([new GameVisualPart { Size = new GameVisualVector(1, 2, 3), Texture = "_content/A/a.jpg" }]);
        var second = new GameVisual([new GameVisualPart { Size = new GameVisualVector(1, 2, 3), Texture = "_content/A/a.jpg" }]);

        // Act
        var isEqual = first.Equals(second);

        // Assert
        Assert.True(isEqual);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void WhenAPartDiffers_ThenTheVisualsDiffer()
    {
        // Arrange
        var first = GameVisual.Box(1, 2, 3);
        var second = GameVisual.Box(1, 2, 4);

        // Act
        var isEqual = first.Equals(second);

        // Assert
        Assert.False(isEqual);
    }

    [Fact]
    public void WhenAVisualIsCreatedFromADefaultArray_ThenItHasNoParts()
    {
        // Act
        var visual = new GameVisual(default(ImmutableArray<GameVisualPart>));

        // Assert
        Assert.Empty(visual.Parts);
    }

    [Fact]
    public void WhenABoxIsCreated_ThenItIsOneBoxOfTheSizeInTheDefaultColor()
    {
        // Act
        var visual = GameVisual.Box(4, 10, 1);

        // Assert
        var part = Assert.Single(visual.Parts);
        Assert.Equal(GameVisualShape.Box, part.Shape);
        Assert.Equal(new GameVisualVector(4, 10, 1), part.Size);
        Assert.Equal(default, part.Offset);
        Assert.Equal(GameVisual.DefaultColor, part.Color);
    }

    [Fact]
    public void WhenTheKeyIsUnchanged_ThenTheCacheReturnsTheSameInstance()
    {
        // Arrange
        var cache = new GameVisualCache<(int Width, int Height)>();
        var first = cache.Get((2, 3), static size => GameVisual.Box(size.Width, size.Height, 1));

        // Act
        var second = cache.Get((2, 3), static size => GameVisual.Box(size.Width, size.Height, 1));
        var third = cache.Get((2, 4), static size => GameVisual.Box(size.Width, size.Height, 1));

        // Assert
        Assert.Same(first, second);
        Assert.NotSame(first, third);
        Assert.Equal(GameVisual.Box(2, 4, 1), third);
    }

    [Fact]
    public void WhenAnObjectHasNoVisualOfItsOwn_ThenItIsAGrayBoxOfItsSize()
    {
        // Arrange
        var gameObject = new Testing.MovementBlocker { Width = 3, Depth = 2, Height = 5 };

        // Act
        var visual = gameObject.Visual;

        // Assert
        Assert.Equal(GameVisual.Box(3, 5, 2), visual);
        Assert.Same(visual, gameObject.Visual);
    }
}
