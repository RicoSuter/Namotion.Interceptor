using HomeBlaze.Game.World;

namespace HomeBlaze.Game.Tests.World;

public class HearingRangeTests
{
    private readonly GameWorldIndex _index = new();
    private readonly SoundPathSearch _search = new();

    private static GamePlayer CreatePlayer(string clientId, int x, int y) =>
        new() { ClientId = clientId, IsConnected = true, X = x, Y = y };

    private double GetVolume(GamePlayer first, GamePlayer second) => HearingRange.GetVolume(first, second, _index, _search);

    private void AddWall(int x, int y, int width, int depth) =>
        _index.AddOrUpdate(new Wall { X = x, Y = y, Width = width, Depth = depth });

    // A room with the inside from -2, 8 to 3, 13 and a door from -1 to 2 in its south wall.
    private Door AddRoomAroundTheOrigin10TilesNorth()
    {
        AddWall(-3, 7, 2, 1);
        AddWall(3, 7, 2, 1);
        AddWall(-3, 14, 8, 1);
        AddWall(-3, 8, 1, 6);
        AddWall(4, 8, 1, 6);
        var door = new Door { X = -1, Y = 7, Width = 4, Depth = 1 };
        _index.AddOrUpdate(door);
        return door;
    }

    [Fact]
    public void WhenThePlayersStandOnTheSameTiles_ThenTheVolumeIsFull()
    {
        // Arrange
        var first = CreatePlayer("a", 0, 0);
        var second = CreatePlayer("b", 0, 0);

        // Act
        var volume = GetVolume(first, second);

        // Assert
        Assert.Equal(1.0, volume, 6);
    }

    [Theory]
    [InlineData(2, 1.0)]
    [InlineData(4, 1.0)]
    [InlineData(8, 0.5)]
    [InlineData(20, 0.2)]
    [InlineData(32, 0.125)]
    public void WhenThePlayersStandInTheOpen_ThenTheVolumeFallsWithTheInverseDistanceBeyond1Meter(int distance, double expected)
    {
        // Arrange
        var first = CreatePlayer("a", 0, 0);
        var second = CreatePlayer("b", distance, 0);

        // Act
        var volume = GetVolume(first, second);

        // Assert
        Assert.Equal(expected, volume, 6);
    }

    [Fact]
    public void WhenTheDistanceApproachesTheRadius_ThenTheVolumeFadesSmoothlyToSilence()
    {
        // Arrange
        const double stepSize = 0.01;
        var gains = new List<double>();

        // Act
        for (var distance = 30.0; distance <= HearingRange.HearingRadius + 1; distance += stepSize)
        {
            gains.Add(HearingRange.GetDistanceGain(distance));
        }

        // Assert
        Assert.Equal(4.0 / 36 * 0.5, HearingRange.GetDistanceGain(36), 6);
        Assert.Equal(0.0, HearingRange.GetDistanceGain(HearingRange.HearingRadius));
        var steps = gains.Zip(gains.Skip(1), (previous, next) => previous - next).ToList();
        Assert.All(steps, step => Assert.InRange(step, 0, 0.0005));
    }

    [Fact]
    public void WhenThePlayersAreJustWithinTheRadius_ThenTheyHearEachOtherFaintly()
    {
        // Arrange
        var first = CreatePlayer("a", 0, 0);
        var second = CreatePlayer("b", 39, 0);

        // Act
        var volume = GetVolume(first, second);

        // Assert
        Assert.InRange(volume, 0.001, 0.01);
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
        var volume = GetVolume(first, second);

        // Assert
        Assert.Equal(0.0, volume);
    }

    [Fact]
    public void WhenAWallStandsBetween_ThenTheVoiceGoesAroundItsEndsAndIsQuieter()
    {
        // Arrange
        AddWall(10, -3, 1, 8);
        var first = CreatePlayer("a", 0, 0);
        var second = CreatePlayer("b", 20, 0);

        // Act
        var volume = GetVolume(first, second);

        // Assert
        // Around an end at 6 or -4, 2.5 tiles longer than the straight 20 tiles.
        var expected = HearingRange.GetDistanceGain(22.5) * HearingRange.GetOcclusionGain(2.5);
        Assert.InRange(volume, expected * 0.8, expected * 1.2);
        Assert.True(volume < 0.2 * 0.7);
    }

    [Fact]
    public void WhenAClosedDoorStandsInAWallBetween_ThenTheVoiceGoesAroundTheWall()
    {
        // Arrange
        AddWall(10, -5, 1, 4);
        _index.AddOrUpdate(new Door { X = 10, Y = -1, Width = 1, Depth = 4 });
        AddWall(10, 3, 1, 4);
        var first = CreatePlayer("a", 0, 0);
        var second = CreatePlayer("b", 20, 0);

        // Act
        var volume = GetVolume(first, second);

        // Assert
        Assert.InRange(volume, 0.01, 0.1);
    }

    [Fact]
    public void WhenAnOpenDoorStandsBetween_ThenTheVoicePasses()
    {
        // Arrange
        _index.AddOrUpdate(new Door { X = 10, Y = -1, Width = 1, Depth = 4, IsOpen = true });
        var first = CreatePlayer("a", 0, 0);
        var second = CreatePlayer("b", 20, 0);

        // Act
        var volume = GetVolume(first, second);

        // Assert
        Assert.Equal(0.2, volume, 6);
    }

    [Fact]
    public void WhenAnotherPlayerStandsBetween_ThenTheVoicePasses()
    {
        // Arrange
        _index.AddOrUpdate(CreatePlayer("c", 10, 0));
        var first = CreatePlayer("a", 0, 0);
        var second = CreatePlayer("b", 20, 0);

        // Act
        var volume = GetVolume(first, second);

        // Assert
        Assert.Equal(0.2, volume, 6);
    }

    [Fact]
    public void WhenThePlayerIsInAClosedRoom_ThenNobodyOutsideHearsIt()
    {
        // Arrange
        AddRoomAroundTheOrigin10TilesNorth();
        var inside = CreatePlayer("a", 0, 10);
        var outside = CreatePlayer("b", 8, 0);

        // Act
        var volume = GetVolume(inside, outside);

        // Assert
        Assert.Equal(0.0, volume);
    }

    [Fact]
    public void WhenTheDoorOfTheRoomOpens_ThenTheVoiceComesThroughTheDoorAndIsQuieter()
    {
        // Arrange
        var door = AddRoomAroundTheOrigin10TilesNorth();
        door.IsOpen = true;
        _index.AddOrUpdate(door);
        var inside = CreatePlayer("a", 0, 10);
        var outside = CreatePlayer("b", 8, 0);

        // Act
        var volume = GetVolume(inside, outside);

        // Assert
        // The centers are 1, 11 and 9, 1, so 12.8 tiles apart; the path bends at the east side of the door.
        var straight = HearingRange.GetDistanceGain(Math.Sqrt(164));
        Assert.InRange(volume, straight * 0.5, straight * 0.95);
    }

    [Fact]
    public void WhenTheVoiceGoesAroundTheBendOfACorridor_ThenItIsQuieterThanTheStraightDistanceGives()
    {
        // Arrange
        // An L-shaped corridor 4 tiles wide: east from x -2 to 13 at y 0 to 3, then north at x 10 to 13 up to y 15.
        AddWall(-3, -1, 18, 1);
        AddWall(-3, 4, 13, 1);
        AddWall(-3, 0, 1, 4);
        AddWall(14, 0, 1, 17);
        AddWall(9, 5, 1, 12);
        AddWall(10, 16, 4, 1);
        var first = CreatePlayer("a", 0, 1);
        var second = CreatePlayer("b", 11, 12);

        // Act
        var volume = GetVolume(first, second);

        // Assert
        // The centers are 15.6 tiles apart; the path past the inner corner at 10, 4 is about 19.5 tiles long.
        var expected = HearingRange.GetDistanceGain(19.5) * HearingRange.GetOcclusionGain(19.5 - Math.Sqrt(242));
        Assert.InRange(volume, expected * 0.8, expected * 1.2);
        Assert.True(volume < HearingRange.GetDistanceGain(Math.Sqrt(242)) * 0.6);
    }

    [Fact]
    public void WhenWallsMeetOnlyAtCorners_ThenTheVoiceDoesNotSlipThroughTheJoints()
    {
        // Arrange
        // A diagonal of single tiles that touch at their corners, longer than any path around it within the radius.
        for (var position = -45; position <= 45; position++)
        {
            AddWall(position, position, 1, 1);
        }

        var first = CreatePlayer("a", 5, -5);
        var second = CreatePlayer("b", -5, 5);

        // Act
        var volume = GetVolume(first, second);

        // Assert
        Assert.Equal(0.0, volume);
    }

    [Fact]
    public void WhenTheLineRunsAlongTheFaceOfAWall_ThenTheWallMakesItQuieter()
    {
        // Arrange
        // The centers lie on the line between rows 0 and 1, which touches this wall in row 0.
        AddWall(10, 0, 1, 1);
        var first = CreatePlayer("a", 0, 0);
        var second = CreatePlayer("b", 20, 0);

        // Act
        var volume = GetVolume(first, second);

        // Assert
        Assert.InRange(volume, 0.1, 0.2 - 0.01);
    }

    [Fact]
    public void WhenADiagonalTouchesTheCornerOfAWall_ThenTheWallMakesItQuieter()
    {
        // Arrange
        // The line from 1, 1 to 11, 11 passes exactly through the corner 6, 6 of this tile.
        AddWall(6, 5, 1, 1);
        var first = CreatePlayer("a", 0, 0);
        var second = CreatePlayer("b", 10, 10);

        // Act
        var volume = GetVolume(first, second);

        // Assert
        Assert.True(volume < 4 / Math.Sqrt(200));
        Assert.True(volume > 0);
    }

    [Fact]
    public void WhenADiagonalPassesNextToTheCornerOfAWall_ThenTheVoicePassesAtFullVolume()
    {
        // Arrange
        AddWall(7, 5, 1, 1);
        var first = CreatePlayer("a", 0, 0);
        var second = CreatePlayer("b", 10, 10);

        // Act
        var volume = GetVolume(first, second);

        // Assert
        Assert.Equal(4 / Math.Sqrt(200), volume, 6);
    }

    [Fact]
    public void WhenALineInNegativeCoordinatesPassesNextToAWall_ThenTheVoicePasses()
    {
        // Arrange
        // The line from -19, -19 to -11, -10 passes at height -14.5 over the right edge of this tile, half a tile below it.
        AddWall(-16, -14, 1, 1);
        var first = CreatePlayer("a", -20, -20);
        var second = CreatePlayer("b", -12, -11);

        // Act
        var volume = GetVolume(first, second);

        // Assert
        Assert.Equal(4 / Math.Sqrt(145), volume, 6);
    }

    [Fact]
    public void WhenAWallOverlapsAPlayer_ThenItDoesNotBlockThatPlayersVoice()
    {
        // Arrange
        AddWall(1, 0, 1, 2);
        var first = CreatePlayer("a", 0, 0);
        var second = CreatePlayer("b", 20, 0);

        // Act
        var volume = GetVolume(first, second);

        // Assert
        Assert.Equal(0.2, volume, 6);
    }

    [Theory]
    [InlineData(0, 0, 10, 10)]
    [InlineData(3, -7, 17, 12)]
    [InlineData(-5, 4, 9, -11)]
    [InlineData(0, 0, 13, 4)]
    [InlineData(0, 0, 20, 0)]
    [InlineData(2, 9, 18, -3)]
    public void WhenTheOrderIsSwapped_ThenTheVolumeIsTheSame(int firstX, int firstY, int secondX, int secondY)
    {
        // Arrange
        AddWall(6, 5, 1, 1);
        AddWall(7, 2, 1, 3);
        AddWall(10, -3, 1, 8);
        AddWall(4, -9, 9, 1);
        var playerA = CreatePlayer("a", firstX, firstY);
        var playerB = CreatePlayer("b", secondX, secondY);

        // Act
        var forward = GetVolume(playerA, playerB);
        var backward = HearingRange.GetVolume(playerB, playerA, _index, new SoundPathSearch());

        // Assert
        Assert.Equal(forward, backward);
    }

    [Fact]
    public void WhenAPlayerStepsBehindTheEndOfAWall_ThenTheVolumeChangesOnlySlightly()
    {
        // Arrange
        // The line from 1, 1 to 21, 7 touches the wall, which ends at the top of row 3; the line to 21, 8 passes above.
        AddWall(10, -10, 1, 14);
        var listener = CreatePlayer("a", 0, 0);
        var blocked = CreatePlayer("b", 20, 6);
        var clear = CreatePlayer("c", 20, 7);

        // Act
        var blockedVolume = GetVolume(listener, blocked);
        var clearVolume = GetVolume(listener, clear);

        // Assert
        Assert.Equal(4 / Math.Sqrt(449), clearVolume, 6);
        Assert.InRange(blockedVolume, clearVolume * 0.95, clearVolume * 1.05);
    }

    [Fact]
    public void WhenAPlayerWalksBehindAWall_ThenEveryStepChangesTheVolumeGradually()
    {
        // Arrange
        AddWall(10, -10, 1, 14);
        var listener = CreatePlayer("a", 0, 0);
        var volumes = new List<double>();

        // Act
        for (var y = 12; y >= -2; y--)
        {
            volumes.Add(GetVolume(listener, CreatePlayer("b", 20, y)));
        }

        // Assert
        var changes = volumes.Zip(volumes.Skip(1), (previous, next) => Math.Abs(next - previous) / previous).ToList();
        Assert.All(changes, change => Assert.InRange(change, 0, 0.15));
        Assert.True(volumes[^1] < volumes[0] * 0.7);
    }

    [Fact]
    public void WhenThePlayersStandAtTheUpperIntLimit_ThenTheVolumeIsComputedWithoutOverflow()
    {
        // Arrange
        var first = CreatePlayer("a", int.MaxValue - 1, int.MaxValue - 1);
        var second = CreatePlayer("b", int.MaxValue - 21, int.MaxValue - 1);

        // Act
        var volume = GetVolume(first, second);

        // Assert
        Assert.Equal(0.2, volume, 6);
    }

    [Fact]
    public void WhenAWallStandsBetweenPlayersAtTheUpperIntLimit_ThenTheVoiceGoesAroundItsLowerEnd()
    {
        // Arrange
        AddWall(int.MaxValue - 11, int.MaxValue - 3, 1, 4);
        var east = CreatePlayer("a", int.MaxValue - 1, int.MaxValue - 1);
        var west = CreatePlayer("b", int.MaxValue - 21, int.MaxValue - 1);

        // Act
        var forward = GetVolume(east, west);
        var backward = GetVolume(west, east);

        // Assert
        Assert.InRange(forward, 0.01, 0.2 * 0.7);
        Assert.Equal(forward, backward);
    }

    [Fact]
    public void WhenAWallStandsBetweenPlayersAtTheLowerIntLimit_ThenTheVoiceGoesAroundItsUpperEnd()
    {
        // Arrange
        AddWall(int.MinValue + 10, int.MinValue, 1, 4);
        var west = CreatePlayer("a", int.MinValue, int.MinValue);
        var east = CreatePlayer("b", int.MinValue + 20, int.MinValue);

        // Act
        var forward = GetVolume(west, east);
        var backward = GetVolume(east, west);

        // Assert
        Assert.InRange(forward, 0.01, 0.2 * 0.7);
        Assert.Equal(forward, backward);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenAWallFromAnIntLimitReachesBeyondTheRadius_ThenTheyDoNotHearEachOther(bool isUpperLimit)
    {
        // Arrange
        var y = isUpperLimit ? int.MaxValue - 60 : int.MinValue;
        var playerY = isUpperLimit ? int.MaxValue - 1 : int.MinValue;
        var x = isUpperLimit ? int.MaxValue - 21 : int.MinValue;
        AddWall(x + 10, y, 1, 61);
        var first = CreatePlayer("a", x, playerY);
        var second = CreatePlayer("b", x + 20, playerY);

        // Act
        var volume = GetVolume(first, second);

        // Assert
        Assert.Equal(0.0, volume);
    }

    [Fact]
    public void WhenAWallStandsOnAVerticalLine_ThenTheVoiceGoesAroundIt()
    {
        // Arrange
        AddWall(-1, 10, 4, 1);
        var first = CreatePlayer("a", 0, 0);
        var second = CreatePlayer("b", 0, 20);

        // Act
        var volume = GetVolume(first, second);

        // Assert
        Assert.InRange(volume, 0.01, 0.2 * 0.8);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void WhenAVerticalLineRunsAlongTheFaceOfAWall_ThenTheWallMakesItQuieter(int wallX)
    {
        // Arrange
        // The centers lie on the line between columns 0 and 1, which touches this wall in either column.
        AddWall(wallX, 10, 1, 1);
        var first = CreatePlayer("a", 0, 0);
        var second = CreatePlayer("b", 0, 20);

        // Act
        var volume = GetVolume(first, second);

        // Assert
        Assert.InRange(volume, 0.1, 0.2 - 0.01);
    }

    [Fact]
    public void WhenThePlayersStandAtOppositeIntLimits_ThenTheyDoNotHearEachOther()
    {
        // Arrange
        var first = CreatePlayer("a", int.MinValue, int.MinValue);
        var second = CreatePlayer("b", int.MaxValue - 1, int.MaxValue - 1);

        // Act
        var volume = GetVolume(first, second);

        // Assert
        Assert.Equal(0.0, volume);
    }

    [Fact]
    public void WhenTheAreaOfAPlayerIsTaken_ThenItReachesTheRadiusBeyondTheFootprint()
    {
        // Arrange
        var footprint = new Footprint(0, 0, 2, 2);
        var footprintAtTheLimits = new Footprint(int.MaxValue - 1, int.MinValue, 2, 2);
        var hugeFootprint = new Footprint(int.MinValue, 0, int.MaxValue, 1);

        // Act
        var area = HearingRange.GetArea(footprint);
        var clamped = HearingRange.GetArea(footprintAtTheLimits);
        var huge = HearingRange.GetArea(hugeFootprint);

        // Assert
        Assert.Equal(new Footprint(-40, -40, 82, 82), area);
        Assert.Equal(new Footprint(int.MaxValue - 41, int.MinValue, 42, 42), clamped);
        Assert.Equal(new Footprint(int.MinValue, -40, int.MaxValue, 81), huge);
    }
}
