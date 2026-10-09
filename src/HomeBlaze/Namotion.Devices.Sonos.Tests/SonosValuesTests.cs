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
        var volume = SonosValues.ToVolume(sonosVolume);

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
        var sonosVolume = SonosValues.ToSonosVolume((decimal)volume);

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
        var adjustment = SonosValues.ToSonosVolumeAdjustment((decimal)delta);

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
    [InlineData("PLAYING", SonosTransportState.Playing)]
    [InlineData("PAUSED_PLAYBACK", SonosTransportState.Paused)]
    [InlineData("STOPPED", SonosTransportState.Stopped)]
    [InlineData("TRANSITIONING", SonosTransportState.Transitioning)]
    [InlineData("SOMETHING_ELSE", SonosTransportState.Unknown)]
    [InlineData(null, SonosTransportState.Unknown)]
    public void WhenParsingTransportState_ThenMapsToEnum(string? value, SonosTransportState expected)
    {
        // Act
        var state = SonosValues.ParseTransportState(value);

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

    [Theory]
    [InlineData(null, SonosSource.None)]
    [InlineData("", SonosSource.None)]
    [InlineData("x-sonos-htastream:RINCON_A0000000000701400:spdif", SonosSource.Tv)]
    [InlineData("x-rincon-stream:RINCON_A0000000000601400", SonosSource.LineIn)]
    [InlineData("x-sonos-vli:RINCON_A0000000000601400:2,spotify:94963e711df088cf", SonosSource.SpotifyConnect)]
    [InlineData("x-sonos-vli:RINCON_A0000000000601400:1,airplay:4F9A2B", SonosSource.AirPlay)]
    [InlineData("x-sonos-vli:RINCON_A0000000000601400:3,unknown:1", SonosSource.Other)]
    [InlineData("x-rincon-mp3radio://stream.example.com/live.mp3", SonosSource.Radio)]
    [InlineData("x-sonosapi-stream:tunein%3a9557?sid=303&flags=8232&sn=1", SonosSource.Radio)]
    [InlineData("aac://https://stream.example.com/live", SonosSource.Radio)]
    [InlineData("https://stream.example.com/live.mp3", SonosSource.Radio)]
    [InlineData("x-rincon-queue:RINCON_A0000000000601400#0", SonosSource.Queue)]
    [InlineData("x-rincon:RINCON_A0000000000101400", SonosSource.Other)]
    public void WhenDetectingSource_ThenMapsUriScheme(string? uri, SonosSource expected)
    {
        // Act
        var source = SonosValues.DetectSource(uri);

        // Assert
        Assert.Equal(expected, source);
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
    public void WhenUriIsRelative_ThenItIsResolvedAgainstTheSpeaker()
    {
        // Act
        var uri = SonosValues.ToAbsoluteUri("/getaa?s=1&u=x-sonos-spotify", new Uri("http://10.0.0.121:1400/"));

        // Assert
        Assert.Equal("http://10.0.0.121:1400/getaa?s=1&u=x-sonos-spotify", uri);
    }

    [Fact]
    public void WhenUriIsAbsolute_ThenItIsKept()
    {
        // Act
        var uri = SonosValues.ToAbsoluteUri("https://images.example.com/cover.jpg", new Uri("http://10.0.0.121:1400/"));

        // Assert
        Assert.Equal("https://images.example.com/cover.jpg", uri);
    }

    [Fact]
    public void WhenConvertingHttpStream_ThenUsesRadioScheme()
    {
        // Act
        var uri = SonosValues.ToStreamUri("http://stream.example.com/live.mp3");

        // Assert
        Assert.Equal("x-rincon-mp3radio://stream.example.com/live.mp3", uri);
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
}
