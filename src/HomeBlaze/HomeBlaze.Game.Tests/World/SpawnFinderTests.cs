using HomeBlaze.Game.World;

namespace HomeBlaze.Game.Tests.World;

public class SpawnFinderTests
{
    [Fact]
    public void WhenStartIsFree_ThenStartIsReturned()
    {
        // Arrange
        var start = new Footprint(0, 0, 2, 2);

        // Act
        var position = SpawnFinder.FindFreePosition(start, _ => true);

        // Assert
        Assert.Equal(start, position);
    }

    [Fact]
    public void WhenStartIsBlocked_ThenTheNearestFreePositionIsReturned()
    {
        // Arrange
        var start = new Footprint(0, 0, 2, 2);
        var blocker = new Footprint(0, 0, 3, 3);

        // Act
        var position = SpawnFinder.FindFreePosition(start, candidate => !candidate.Intersects(blocker));

        // Assert
        Assert.False(position.Intersects(blocker));
        Assert.True(Math.Max(Math.Abs(position.X), Math.Abs(position.Y)) <= 3);
    }

    [Fact]
    public void WhenNothingIsFree_ThenStartIsReturned()
    {
        // Arrange
        var start = new Footprint(5, 5, 2, 2);

        // Act
        var position = SpawnFinder.FindFreePosition(start, _ => false, maximumRadius: 3);

        // Assert
        Assert.Equal(start, position);
    }
}
