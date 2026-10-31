using HomeBlaze.Game.World;

namespace HomeBlaze.Game.Tests.World;

public class TargetAreaTests
{
    [Fact]
    public void WhenFacingNorth_ThenTheTargetIsTheTwoTilesInFront()
    {
        // Arrange
        var tiles = new List<Footprint>();

        // Act
        TargetArea.GetTiles(new Footprint(0, 0, 2, 2), GameDirection.North, tiles);

        // Assert
        Assert.Equal(new[] { new Footprint(0, 2, 1, 1), new Footprint(1, 2, 1, 1) }, tiles);
    }

    [Fact]
    public void WhenFacingNorthEast_ThenTheTargetIsTheThreeTilesAroundTheCorner()
    {
        // Arrange
        var tiles = new List<Footprint>();

        // Act
        TargetArea.GetTiles(new Footprint(0, 0, 2, 2), GameDirection.NorthEast, tiles);

        // Assert
        Assert.Equal(new[] { new Footprint(2, 1, 1, 1), new Footprint(1, 2, 1, 1), new Footprint(2, 2, 1, 1) }, tiles);
    }

    [Fact]
    public void WhenTheTargetWouldLeaveTheIntRange_ThenThereIsNoTarget()
    {
        // Arrange
        var tiles = new List<Footprint>();

        // Act
        TargetArea.GetTiles(new Footprint(int.MaxValue - 1, 0, 2, 2), GameDirection.East, tiles);

        // Assert
        Assert.Empty(tiles);
    }
}
