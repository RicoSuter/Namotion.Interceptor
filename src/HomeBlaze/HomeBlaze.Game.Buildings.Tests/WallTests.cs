namespace HomeBlaze.Game.Buildings.Tests;

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
        Assert.Equal("_content/HomeBlaze.Game.Buildings/textures/bricks.jpg", part.Texture);
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

    [Theory]
    [InlineData(4, false)]
    [InlineData(8, true)]
    public void WhenAWallHasAHeight_ThenOnlyAWallOfAtLeastTwoMetersBlocksSound(int height, bool isSoundBlocking)
    {
        // Act
        var wall = new Wall { Height = height };

        // Assert
        Assert.Equal(isSoundBlocking, wall.IsSoundBlocking);
    }

    [Theory]
    [InlineData(WallMaterial.Bricks, "bricks.jpg", 8)]
    [InlineData(WallMaterial.Concrete, "concrete.jpg", 10)]
    [InlineData(WallMaterial.Wood, "wood.jpg", 6)]
    public void WhenTheMaterialIsSet_ThenTheWallShowsItsTexture(WallMaterial material, string file, double scale)
    {
        // Arrange
        var wall = new Wall();

        // Act
        wall.Material = material;

        // Assert
        var part = Assert.Single(wall.Visual.Parts);
        Assert.Equal($"_content/HomeBlaze.Game.Buildings/textures/{file}", part.Texture);
        Assert.Equal(scale, part.TextureScale);
    }

    [Fact]
    public void WhenTheMaterialIsUnknown_ThenTheWallShowsBricks()
    {
        // Arrange
        var wall = new Wall();

        // Act
        wall.Material = (WallMaterial)7;

        // Assert
        Assert.Equal("_content/HomeBlaze.Game.Buildings/textures/bricks.jpg", Assert.Single(wall.Visual.Parts).Texture);
    }

    [Fact]
    public void WhenADoorHasAMaterial_ThenItsLintelShowsIt()
    {
        // Arrange
        var door = new Door { Material = WallMaterial.Wood };

        // Act
        var lintel = door.Visual.Parts[0];

        // Assert
        Assert.Equal("_content/HomeBlaze.Game.Buildings/textures/wood.jpg", lintel.Texture);
    }
}
