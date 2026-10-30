using HomeBlaze.Game.World;

namespace HomeBlaze.Game.Tests.World;

public class FootprintTests
{
    [Fact]
    public void WhenFootprintsOverlap_ThenTheyIntersect()
    {
        // Arrange
        var first = new Footprint(0, 0, 2, 2);
        var second = new Footprint(1, 1, 4, 1);

        // Act
        var intersects = first.Intersects(second);

        // Assert
        Assert.True(intersects);
    }

    [Fact]
    public void WhenFootprintsOnlyTouch_ThenTheyDoNotIntersect()
    {
        // Arrange
        var first = new Footprint(0, 0, 2, 2);
        var second = new Footprint(2, 0, 2, 2);

        // Act
        var intersects = first.Intersects(second);

        // Assert
        Assert.False(intersects);
    }

    [Fact]
    public void WhenTileIsInside_ThenContainsIsTrue()
    {
        // Arrange
        var footprint = new Footprint(-3, -3, 2, 2);

        // Act & Assert
        Assert.True(footprint.Contains(-2, -2));
        Assert.False(footprint.Contains(-1, -2));
    }

    [Fact]
    public void WhenMoveStaysInRange_ThenItSucceeds()
    {
        // Arrange
        var footprint = new Footprint(5, 5, 2, 2);

        // Act
        var isMoved = footprint.TryMove(-1, 1, out var moved);

        // Assert
        Assert.True(isMoved);
        Assert.Equal(new Footprint(4, 6, 2, 2), moved);
    }

    [Fact]
    public void WhenMoveLeavesTheIntRangeToTheEast_ThenItFails()
    {
        // Arrange
        var footprint = new Footprint(int.MaxValue - 1, 0, 2, 2);

        // Act
        var isMoved = footprint.TryMove(1, 0, out _);

        // Assert
        Assert.False(isMoved);
    }

    [Fact]
    public void WhenMoveLeavesTheIntRangeToTheSouth_ThenItFails()
    {
        // Arrange
        var footprint = new Footprint(0, int.MinValue, 2, 2);

        // Act
        var isMoved = footprint.TryMove(0, -1, out _);

        // Assert
        Assert.False(isMoved);
    }

    [Fact]
    public void WhenFootprintReachesTheEdge_ThenRightIsComputedWithoutOverflow()
    {
        // Arrange
        var footprint = new Footprint(int.MaxValue, 0, 1, 1);

        // Act
        var right = footprint.Right;

        // Assert
        Assert.Equal(int.MaxValue, right);
    }
}
