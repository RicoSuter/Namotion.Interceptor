using HomeBlaze.Game.World;

namespace HomeBlaze.Game.Tests.World;

public class FrontPlacementTests
{
    [Theory]
    [InlineData(GameDirection.North, -1, 2, 4, 1)]
    [InlineData(GameDirection.South, -1, -1, 4, 1)]
    [InlineData(GameDirection.East, 2, -1, 1, 4)]
    [InlineData(GameDirection.West, -1, -1, 1, 4)]
    public void WhenFacingStraight_ThenTheFootprintStandsAcrossTheFacingInFront(
        GameDirection facing, int x, int y, int width, int depth)
    {
        // Act
        var isPlaced = FrontPlacement.TryGetFootprint(new GameArea(0, 0, 2, 2), facing, across: 4, along: 1, out var footprint);

        // Assert
        Assert.True(isPlaced);
        Assert.Equal(new GameArea(x, y, width, depth), footprint);
    }

    [Theory]
    [InlineData(GameDirection.North, 1, 2, 1, 1)]
    [InlineData(GameDirection.South, 1, -1, 1, 1)]
    [InlineData(GameDirection.East, 2, 1, 1, 1)]
    [InlineData(GameDirection.West, -1, 1, 1, 1)]
    public void WhenTheObjectIsOneTileAcross_ThenTheOddRemainderGoesEastOrNorth(
        GameDirection facing, int x, int y, int width, int depth)
    {
        // Act
        var isPlaced = FrontPlacement.TryGetFootprint(new GameArea(0, 0, 2, 2), facing, across: 1, along: 1, out var footprint);

        // Assert
        Assert.True(isPlaced);
        Assert.Equal(new GameArea(x, y, width, depth), footprint);
    }

    [Theory]
    [InlineData(GameDirection.North, 0, 2, 3, 1)]
    [InlineData(GameDirection.South, 0, -1, 3, 1)]
    [InlineData(GameDirection.East, 2, 0, 1, 3)]
    [InlineData(GameDirection.West, -1, 0, 1, 3)]
    public void WhenTheObjectIsThreeTilesAcross_ThenTheOddRemainderGoesEastOrNorth(
        GameDirection facing, int x, int y, int width, int depth)
    {
        // Act
        var isPlaced = FrontPlacement.TryGetFootprint(new GameArea(0, 0, 2, 2), facing, across: 3, along: 1, out var footprint);

        // Assert
        Assert.True(isPlaced);
        Assert.Equal(new GameArea(x, y, width, depth), footprint);
    }

    [Fact]
    public void WhenTheFootprintIsAsWideAsThePlayer_ThenItIsCenteredInFront()
    {
        // Act
        var isPlaced = FrontPlacement.TryGetFootprint(new GameArea(0, 0, 2, 2), GameDirection.East, across: 2, along: 3, out var footprint);

        // Assert
        Assert.True(isPlaced);
        Assert.Equal(new GameArea(2, 0, 3, 2), footprint);
    }

    [Fact]
    public void WhenFacingDiagonally_ThenNothingIsPlaced()
    {
        // Act
        var isPlaced = FrontPlacement.TryGetFootprint(new GameArea(0, 0, 2, 2), GameDirection.NorthEast, across: 4, along: 1, out _);

        // Assert
        Assert.False(isPlaced);
    }

    [Fact]
    public void WhenTheFootprintWouldLeaveTheIntRange_ThenNothingIsPlaced()
    {
        // Act
        var isPlaced = FrontPlacement.TryGetFootprint(new GameArea(0, int.MaxValue - 1, 2, 2), GameDirection.North, across: 1, along: 1, out _);

        // Assert
        Assert.False(isPlaced);
    }
}
