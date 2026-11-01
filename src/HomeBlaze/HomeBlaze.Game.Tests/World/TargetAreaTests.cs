using HomeBlaze.Game.World;

namespace HomeBlaze.Game.Tests.World;

public class TargetAreaTests
{
    [Fact]
    public void WhenFacingNorth_ThenTheTargetIsTheTwoTilesInFront()
    {
        // Arrange
        var tiles = new List<GameArea>();

        // Act
        TargetArea.GetTiles(new GameArea(0, 0, 2, 2), GameDirection.North, tiles);

        // Assert
        Assert.Equal(new[] { new GameArea(0, 2, 1, 1), new GameArea(1, 2, 1, 1) }, tiles);
    }

    [Fact]
    public void WhenFacingNorthEast_ThenTheTargetIsTheThreeTilesAroundTheCorner()
    {
        // Arrange
        var tiles = new List<GameArea>();

        // Act
        TargetArea.GetTiles(new GameArea(0, 0, 2, 2), GameDirection.NorthEast, tiles);

        // Assert
        Assert.Equal(new[] { new GameArea(2, 1, 1, 1), new GameArea(1, 2, 1, 1), new GameArea(2, 2, 1, 1) }, tiles);
    }

    [Fact]
    public void WhenTheTargetWouldLeaveTheIntRange_ThenThereIsNoTarget()
    {
        // Arrange
        var tiles = new List<GameArea>();

        // Act
        TargetArea.GetTiles(new GameArea(int.MaxValue - 1, 0, 2, 2), GameDirection.East, tiles);

        // Assert
        Assert.Empty(tiles);
    }
}
