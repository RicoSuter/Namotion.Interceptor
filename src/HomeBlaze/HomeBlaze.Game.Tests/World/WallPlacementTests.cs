using HomeBlaze.Game.World;

namespace HomeBlaze.Game.Tests.World;

public class WallPlacementTests
{
    [Theory]
    [InlineData(GameDirection.North, -1, 2, 4, 1)]
    [InlineData(GameDirection.South, -1, -1, 4, 1)]
    [InlineData(GameDirection.East, 2, -1, 1, 4)]
    [InlineData(GameDirection.West, -1, -1, 1, 4)]
    public void WhenFacingStraight_ThenTheWallStandsAcrossTheFacingDirectionInFront(
        GameDirection facing, int x, int y, int width, int depth)
    {
        // Act
        var isPlaced = WallPlacement.TryGetFootprint(new Footprint(0, 0, 2, 2), facing, out var wall);

        // Assert
        Assert.True(isPlaced);
        Assert.Equal(new Footprint(x, y, width, depth), wall);
    }

    [Fact]
    public void WhenFacingDiagonally_ThenNoWallIsPlaced()
    {
        // Act
        var isPlaced = WallPlacement.TryGetFootprint(new Footprint(0, 0, 2, 2), GameDirection.NorthEast, out _);

        // Assert
        Assert.False(isPlaced);
    }
}
