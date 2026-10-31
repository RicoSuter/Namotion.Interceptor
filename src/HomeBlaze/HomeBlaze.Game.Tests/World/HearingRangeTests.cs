using HomeBlaze.Game.World;

namespace HomeBlaze.Game.Tests.World;

public class HearingRangeTests
{
    private readonly GameWorldIndex _index = new();

    private static GamePlayer CreatePlayer(string clientId, int x, int y) =>
        new() { ClientId = clientId, IsConnected = true, X = x, Y = y };

    [Fact]
    public void WhenThePlayersStandOnTheSameTiles_ThenTheVolumeIsFull()
    {
        // Arrange
        var first = CreatePlayer("a", 0, 0);
        var second = CreatePlayer("b", 0, 0);

        // Act
        var volume = HearingRange.GetVolume(first, second, _index);

        // Assert
        Assert.Equal(1.0, volume, 6);
    }

    [Fact]
    public void WhenThePlayersAre20TilesApart_ThenTheVolumeIsHalf()
    {
        // Arrange
        var first = CreatePlayer("a", 0, 0);
        var second = CreatePlayer("b", 20, 0);

        // Act
        var volume = HearingRange.GetVolume(first, second, _index);

        // Assert
        Assert.Equal(0.5, volume, 6);
    }

    [Fact]
    public void WhenThePlayersAreJustWithinTheRadius_ThenTheyHearEachOtherFaintly()
    {
        // Arrange
        var first = CreatePlayer("a", 0, 0);
        var second = CreatePlayer("b", 39, 0);

        // Act
        var volume = HearingRange.GetVolume(first, second, _index);

        // Assert
        Assert.Equal(0.025, volume, 6);
    }

    [Theory]
    [InlineData(40, 0)]
    [InlineData(41, 0)]
    [InlineData(30, 30)]
    public void WhenThePlayersAreAtLeastTheRadiusApart_ThenTheyDoNotHearEachOther(int x, int y)
    {
        // Arrange
        var first = CreatePlayer("a", 0, 0);
        var second = CreatePlayer("b", x, y);

        // Act
        var volume = HearingRange.GetVolume(first, second, _index);

        // Assert
        Assert.Equal(0.0, volume);
    }

    [Fact]
    public void WhenAWallStandsBetween_ThenTheyDoNotHearEachOther()
    {
        // Arrange
        _index.AddOrUpdate(new Wall { X = 10, Y = -1, Width = 1, Depth = 4 });
        var first = CreatePlayer("a", 0, 0);
        var second = CreatePlayer("b", 20, 0);

        // Act
        var volume = HearingRange.GetVolume(first, second, _index);

        // Assert
        Assert.Equal(0.0, volume);
    }

    [Fact]
    public void WhenAClosedDoorStandsBetween_ThenTheyDoNotHearEachOther()
    {
        // Arrange
        _index.AddOrUpdate(new Door { X = 10, Y = -1, Width = 1, Depth = 4 });
        var first = CreatePlayer("a", 0, 0);
        var second = CreatePlayer("b", 20, 0);

        // Act
        var volume = HearingRange.GetVolume(first, second, _index);

        // Assert
        Assert.Equal(0.0, volume);
    }

    [Fact]
    public void WhenAnOpenDoorStandsBetween_ThenTheVoicePasses()
    {
        // Arrange
        _index.AddOrUpdate(new Door { X = 10, Y = -1, Width = 1, Depth = 4, IsOpen = true });
        var first = CreatePlayer("a", 0, 0);
        var second = CreatePlayer("b", 20, 0);

        // Act
        var volume = HearingRange.GetVolume(first, second, _index);

        // Assert
        Assert.Equal(0.5, volume, 6);
    }

    [Fact]
    public void WhenAnotherPlayerStandsBetween_ThenTheVoicePasses()
    {
        // Arrange
        _index.AddOrUpdate(CreatePlayer("c", 10, 0));
        var first = CreatePlayer("a", 0, 0);
        var second = CreatePlayer("b", 20, 0);

        // Act
        var volume = HearingRange.GetVolume(first, second, _index);

        // Assert
        Assert.Equal(0.5, volume, 6);
    }

    [Fact]
    public void WhenTheLineRunsAlongTheFaceOfAWall_ThenTheWallBlocks()
    {
        // Arrange
        // The centers lie on the line between rows 0 and 1, which touches this wall in row 0.
        _index.AddOrUpdate(new Wall { X = 10, Y = 0, Width = 1, Depth = 1 });
        var first = CreatePlayer("a", 0, 0);
        var second = CreatePlayer("b", 20, 0);

        // Act
        var volume = HearingRange.GetVolume(first, second, _index);

        // Assert
        Assert.Equal(0.0, volume);
    }

    [Fact]
    public void WhenADiagonalTouchesTheCornerOfAWall_ThenTheWallBlocks()
    {
        // Arrange
        // The line from 1, 1 to 11, 11 passes exactly through the corner 6, 6 of this tile.
        _index.AddOrUpdate(new Wall { X = 6, Y = 5, Width = 1, Depth = 1 });
        var first = CreatePlayer("a", 0, 0);
        var second = CreatePlayer("b", 10, 10);

        // Act
        var volume = HearingRange.GetVolume(first, second, _index);

        // Assert
        Assert.Equal(0.0, volume);
    }

    [Fact]
    public void WhenADiagonalPassesNextToTheCornerOfAWall_ThenTheVoicePasses()
    {
        // Arrange
        _index.AddOrUpdate(new Wall { X = 7, Y = 5, Width = 1, Depth = 1 });
        var first = CreatePlayer("a", 0, 0);
        var second = CreatePlayer("b", 10, 10);

        // Act
        var volume = HearingRange.GetVolume(first, second, _index);

        // Assert
        Assert.Equal(1 - Math.Sqrt(200) / HearingRange.HearingRadius, volume, 6);
    }

    [Fact]
    public void WhenAWallOverlapsAPlayer_ThenItDoesNotBlockThatPlayersVoice()
    {
        // Arrange
        _index.AddOrUpdate(new Wall { X = 1, Y = 0, Width = 1, Depth = 2 });
        var first = CreatePlayer("a", 0, 0);
        var second = CreatePlayer("b", 20, 0);

        // Act
        var volume = HearingRange.GetVolume(first, second, _index);

        // Assert
        Assert.Equal(0.5, volume, 6);
    }

    [Theory]
    [InlineData(0, 0, 10, 10)]
    [InlineData(3, -7, 17, 12)]
    [InlineData(-5, 4, 9, -11)]
    [InlineData(0, 0, 13, 4)]
    public void WhenTheOrderIsSwapped_ThenTheVolumeIsTheSame(int firstX, int firstY, int secondX, int secondY)
    {
        // Arrange
        _index.AddOrUpdate(new Wall { X = 6, Y = 5, Width = 1, Depth = 1 });
        _index.AddOrUpdate(new Wall { X = 7, Y = 2, Width = 1, Depth = 3 });
        var playerA = CreatePlayer("a", firstX, firstY);
        var playerB = CreatePlayer("b", secondX, secondY);

        // Act
        var forward = HearingRange.GetVolume(playerA, playerB, _index);
        var backward = HearingRange.GetVolume(playerB, playerA, _index);

        // Assert
        Assert.Equal(forward, backward);
    }

    [Fact]
    public void WhenThePlayersStandAtTheUpperIntLimit_ThenTheVolumeIsComputedWithoutOverflow()
    {
        // Arrange
        var first = CreatePlayer("a", int.MaxValue - 1, int.MaxValue - 1);
        var second = CreatePlayer("b", int.MaxValue - 21, int.MaxValue - 1);

        // Act
        var volume = HearingRange.GetVolume(first, second, _index);

        // Assert
        Assert.Equal(0.5, volume, 6);
    }

    [Fact]
    public void WhenAWallStandsBetweenPlayersAtTheUpperIntLimit_ThenItBlocks()
    {
        // Arrange
        _index.AddOrUpdate(new Wall { X = int.MaxValue - 11, Y = int.MaxValue - 3, Width = 1, Depth = 4 });
        var first = CreatePlayer("a", int.MaxValue - 1, int.MaxValue - 1);
        var second = CreatePlayer("b", int.MaxValue - 21, int.MaxValue - 1);

        // Act
        var volume = HearingRange.GetVolume(first, second, _index);

        // Assert
        Assert.Equal(0.0, volume);
    }

    [Fact]
    public void WhenThePlayersStandAtOppositeIntLimits_ThenTheyDoNotHearEachOther()
    {
        // Arrange
        var first = CreatePlayer("a", int.MinValue, int.MinValue);
        var second = CreatePlayer("b", int.MaxValue - 1, int.MaxValue - 1);

        // Act
        var volume = HearingRange.GetVolume(first, second, _index);

        // Assert
        Assert.Equal(0.0, volume);
    }

    [Fact]
    public void WhenTheAreaOfAPlayerIsTaken_ThenItReachesTheRadiusBeyondTheFootprint()
    {
        // Act
        var area = HearingRange.GetArea(new Footprint(0, 0, 2, 2));
        var clamped = HearingRange.GetArea(new Footprint(int.MaxValue - 1, int.MinValue, 2, 2));

        // Assert
        Assert.Equal(new Footprint(-40, -40, 82, 82), area);
        Assert.Equal(((long)int.MaxValue, (long)int.MinValue), (clamped.Right, (long)clamped.Y));
    }
}
