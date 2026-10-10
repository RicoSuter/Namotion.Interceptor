using HomeBlaze.Abstractions.Media;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class SonosValuesTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(44, 0.44)]
    [InlineData(100, 1)]
    [InlineData(150, 1)]
    public void WhenConvertingSonosVolume_ThenReturnsFraction(int sonosVolume, double expected)
    {
        // Act
        var volume = SonosValues.ToFraction(sonosVolume);

        // Assert
        Assert.Equal((decimal)expected, volume);
    }

    [Theory]
    [InlineData(0.5, 50)]
    [InlineData(0.444, 44)]
    [InlineData(0.445, 45)]
    [InlineData(-1, 0)]
    [InlineData(2, 100)]
    public void WhenConvertingVolumeFraction_ThenReturnsClampedSonosVolume(double volume, int expected)
    {
        // Act
        var sonosVolume = SonosValues.ToSonosPercent((decimal)volume, 0m);

        // Assert
        Assert.Equal(expected, sonosVolume);
    }

    [Theory]
    [InlineData(0.05, 5)]
    [InlineData(-0.1, -10)]
    [InlineData(-3, -100)]
    public void WhenConvertingVolumeDelta_ThenReturnsClampedAdjustment(double delta, int expected)
    {
        // Act
        var adjustment = SonosValues.ToSonosPercent((decimal)delta, -1m);

        // Assert
        Assert.Equal(expected, adjustment);
    }

    [Theory]
    [InlineData("0:03:25", 205)]
    [InlineData("00:00:01", 1)]
    [InlineData("26:00:00", 93600)]
    [InlineData("0:00:10.500", 10.5)]
    public void WhenParsingDuration_ThenReturnsTimeSpan(string value, double expectedSeconds)
    {
        // Act
        var duration = SonosValues.ParseDuration(value);

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), duration);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NOT_IMPLEMENTED")]
    [InlineData("garbage")]
    [InlineData("0:03")]
    [InlineData("0:00:03:25")]
    [InlineData("0:00:03:25:00")]
    [InlineData("2147483647:00:00")]
    [InlineData("0:00:99999999999999999999")]
    [InlineData("0:60:00")]
    [InlineData("0:00:60")]
    [InlineData("0:00:NaN")]
    [InlineData("0:00:Infinity")]
    [InlineData("0:00:-Infinity")]
    public void WhenParsingUnknownDuration_ThenReturnsNull(string? value)
    {
        // Act
        var duration = SonosValues.ParseDuration(value);

        // Assert
        Assert.Null(duration);
    }

    [Fact]
    public void WhenFormattingDuration_ThenUsesTwoDigitHours()
    {
        // Act
        var formatted = SonosValues.FormatDuration(new TimeSpan(1, 2, 3));

        // Assert
        Assert.Equal("01:02:03", formatted);
    }

    [Theory]
    [InlineData("PLAYING", MediaPlaybackState.Playing)]
    [InlineData("PAUSED_PLAYBACK", MediaPlaybackState.Paused)]
    [InlineData("STOPPED", MediaPlaybackState.Stopped)]
    [InlineData("TRANSITIONING", MediaPlaybackState.Buffering)]
    [InlineData("SOMETHING_ELSE", null)]
    [InlineData(null, null)]
    public void WhenParsingPlaybackState_ThenMapsToEnumOrUnknown(string? value, MediaPlaybackState? expected)
    {
        // Act
        var state = SonosValues.ParsePlaybackState(value);

        // Assert
        Assert.Equal(expected, state);
    }

    [Theory]
    [InlineData("NORMAL", false, SonosRepeatMode.Off)]
    [InlineData("REPEAT_ALL", false, SonosRepeatMode.All)]
    [InlineData("REPEAT_ONE", false, SonosRepeatMode.One)]
    [InlineData("SHUFFLE_NOREPEAT", true, SonosRepeatMode.Off)]
    [InlineData("SHUFFLE", true, SonosRepeatMode.All)]
    [InlineData("SHUFFLE_REPEAT_ONE", true, SonosRepeatMode.One)]
    public void WhenParsingAndFormattingPlayMode_ThenRoundTrips(string playMode, bool shuffle, SonosRepeatMode repeat)
    {
        // Act
        var parsed = SonosValues.ParsePlayMode(playMode);
        var formatted = SonosValues.FormatPlayMode(shuffle, repeat);

        // Assert
        Assert.Equal((shuffle, repeat), parsed);
        Assert.Equal(playMode, formatted);
    }

    [Fact]
    public void WhenParsingUnknownPlayMode_ThenReturnsNull()
    {
        // Act
        var parsed = SonosValues.ParsePlayMode("NOT_IMPLEMENTED");

        // Assert
        Assert.Null(parsed);
    }

    [Fact]
    public void WhenParsingBatteryInfo_ThenReturnsLevelAndCharging()
    {
        // Act
        var (level, isCharging) = SonosValues.ParseBattery("RawBattPct:100,BattPct:87,BattChg:CHARGING,BattTmp:23");

        // Assert
        Assert.Equal(0.87m, level);
        Assert.True(isCharging);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("SomethingElse:1")]
    public void WhenParsingMissingBatteryInfo_ThenReturnsNulls(string? moreInfo)
    {
        // Act
        var (level, isCharging) = SonosValues.ParseBattery(moreInfo);

        // Assert
        Assert.Null(level);
        Assert.Null(isCharging);
    }

    [Fact]
    public void WhenBatteryNotCharging_ThenIsChargingIsFalse()
    {
        // Act
        var (_, isCharging) = SonosValues.ParseBattery("BattPct:40,BattChg:NOT_CHARGING");

        // Assert
        Assert.False(isCharging);
    }

    [Fact]
    public void WhenCreatingStreamMetadata_ThenTitleIsEscaped()
    {
        // Act
        var metadata = SonosValues.CreateStreamMetadata("Rock & <Roll>");

        // Assert
        Assert.Contains("<dc:title>Rock &amp; &lt;Roll&gt;</dc:title>", metadata);
        Assert.Contains("object.item.audioItem.audioBroadcast", metadata);
    }

    [Fact]
    public void WhenCreatingStreamMetadataWithoutTitle_ThenTitleIsEmpty()
    {
        // Act
        var metadata = SonosValues.CreateStreamMetadata(null);

        // Assert
        Assert.Contains("<dc:title></dc:title>", metadata);
    }
}
