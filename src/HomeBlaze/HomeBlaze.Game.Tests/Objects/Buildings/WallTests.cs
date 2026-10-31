namespace HomeBlaze.Game.Tests.Objects.Buildings;

public class WallTests
{
    [Fact]
    public void WhenAWallIsCreated_ThenItLooksLikeABrickBoxOfItsSize()
    {
        // Arrange
        var wall = new Wall { Width = 6, Depth = 1, Height = 10 };

        // Act
        var part = Assert.Single(wall.Visual.Parts);

        // Assert
        Assert.Equal(GameVisualShape.Box, part.Shape);
        Assert.Equal(new GameVisualVector(6, 10, 1), part.Size);
        Assert.Equal("_content/HomeBlaze.Game/objects/buildings/textures/bricks.jpg", part.Texture);
        Assert.Equal(8, part.TextureScale);
    }

    [Fact]
    public void WhenTheWallIsUnchanged_ThenItsVisualIsTheSameInstance()
    {
        // Arrange
        var wall = new Wall();
        var first = wall.Visual;

        // Act
        var second = wall.Visual;

        // Assert
        Assert.Same(first, second);
    }

    [Fact]
    public void WhenTheWallIsResized_ThenItsVisualFollows()
    {
        // Arrange
        var wall = new Wall();

        // Act
        wall.Width = 1;
        wall.Depth = 12;
        wall.Height = 4;

        // Assert
        Assert.Equal(new GameVisualVector(1, 4, 12), Assert.Single(wall.Visual.Parts).Size);
    }
}
