namespace HomeBlaze.Game.Tests;

public class GameDirectionTests
{
    [Theory]
    [InlineData(GameDirection.North, 0, 1)]
    [InlineData(GameDirection.NorthEast, 1, 1)]
    [InlineData(GameDirection.East, 1, 0)]
    [InlineData(GameDirection.SouthEast, 1, -1)]
    [InlineData(GameDirection.South, 0, -1)]
    [InlineData(GameDirection.SouthWest, -1, -1)]
    [InlineData(GameDirection.West, -1, 0)]
    [InlineData(GameDirection.NorthWest, -1, 1)]
    public void WhenDirectionIsConverted_ThenOffsetPointsThatWay(GameDirection direction, int expectedX, int expectedY)
    {
        // Act
        var offsetX = direction.GetOffsetX();
        var offsetY = direction.GetOffsetY();

        // Assert
        Assert.Equal(expectedX, offsetX);
        Assert.Equal(expectedY, offsetY);
    }

    [Theory]
    [InlineData(GameDirection.North, false)]
    [InlineData(GameDirection.NorthEast, true)]
    [InlineData(GameDirection.West, false)]
    [InlineData(GameDirection.SouthWest, true)]
    public void WhenDirectionIsChecked_ThenOnlyMixedDirectionsAreDiagonal(GameDirection direction, bool expected)
    {
        // Act
        var isDiagonal = direction.IsDiagonal();

        // Assert
        Assert.Equal(expected, isDiagonal);
    }

    [Fact]
    public void WhenShortNameIsRequested_ThenCompassAbbreviationIsReturned()
    {
        // Act
        var name = GameDirection.SouthEast.GetShortName();

        // Assert
        Assert.Equal("SE", name);
    }
}
