using Namotion.Devices.Sonos.Tests.Testing;
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
    [InlineData(0, 30)]
    [InlineData(-10, 30)]
    [InlineData(1, 5)]
    [InlineData(5, 5)]
    [InlineData(600, 600)]
    [InlineData(3600, 3600)]
    [InlineData(7200, 3600)]
    public void WhenIntervalIsConfigured_ThenItIsClampedOrFallsBackToTheDefault(int configuredSeconds, int expectedSeconds)
    {
        // Act
        var interval = SonosValues.GetEffectiveInterval(TimeSpan.FromSeconds(configuredSeconds), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5));

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), interval);
    }

    [Fact]
    public void WhenIntervalIsTheLargestTimeSpan_ThenItIsClampedToOneHour()
    {
        // Act
        var interval = SonosValues.GetEffectiveInterval(TimeSpan.MaxValue, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5));

        // Assert
        Assert.Equal(TimeSpan.FromHours(1), interval);
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
    [InlineData(TestFixtures.SpotifyConnectUri, SonosSource.SpotifyConnect)]
    [InlineData("x-sonos-vli:RINCON_A0000000000601400:1,airplay:4F9A2B", SonosSource.AirPlay)]
    [InlineData("x-sonos-vli:RINCON_A0000000000601400:3,unknown:1", SonosSource.Other)]
    [InlineData("x-rincon-mp3radio://stream.example.com/live.mp3", SonosSource.Radio)]
    [InlineData("x-sonosapi-stream:tunein%3a9557?sid=303&flags=8232&sn=1", SonosSource.Radio)]
    [InlineData("aac://https://stream.example.com/live", SonosSource.Radio)]
    [InlineData("x-sonosapi-radio:radio%3a1?sid=236", SonosSource.Radio)]
    [InlineData("x-sonosapi-hls:live%3a1?sid=284", SonosSource.Radio)]
    [InlineData("hls-radio://stream.example.com/live.m3u8", SonosSource.Radio)]
    [InlineData("https://stream.example.com/live.mp3", SonosSource.Other)]
    [InlineData("http://files.example.com/chime.mp3", SonosSource.Other)]
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

    [Fact]
    public void WhenCreatingStreamMetadataWithoutTitle_ThenTitleIsEmpty()
    {
        // Act
        var metadata = SonosValues.CreateStreamMetadata(null);

        // Assert
        Assert.Contains("<dc:title></dc:title>", metadata);
    }

    [Theory]
    [InlineData("https://host.example/live.mp3", "x-rincon-mp3radio://host.example/live.mp3", true)]
    [InlineData("host.example/live.mp3", "x-rincon-mp3radio://host.example/live.mp3", true)]
    [InlineData("x-rincon-mp3radio://host.example/live.mp3", "x-rincon-mp3radio://host.example/live.mp3", true)]
    [InlineData("http://host.example/live", "aac://https://host.example/live", true)]
    [InlineData("host.example/beep.mp3", "https://host.example/beep.mp3", true)]
    [InlineData("96", "aac://http://host.example/aac/96", false)]
    [InlineData("live.mp3", "x-rincon-mp3radio://host.example/live.mp3", false)]
    [InlineData("https://host.example/live.mp3", "x-rincon-mp3radio://other.example/live.mp3", false)]
    [InlineData("host.example", "x-rincon-mp3radio://host.example/live.mp3", false)]
    [InlineData("https://host.example/song.mp3", "x-file-cifs://host.example/song.mp3", false)]
    [InlineData("https://host.example/live.mp3", null, false)]
    public void WhenTitleIsTheStreamUri_ThenItIsNotATitle(string title, string? uri, bool expected)
    {
        // Act
        var isUri = SonosValues.IsStreamUri(title, uri);

        // Assert
        Assert.Equal(expected, isUri);
    }

    private const string StationUri = "x-sonosapi-stream:s1?sid=303";
    private const string QueueUri = "x-rincon-queue:RINCON_A0000000000601400#0";
    private const string AdUri = "https://ads.example/preroll.mp3";
    private const string AdWithQueryUri = "https://ads.example/preroll.mp3?player=sonos&language=en%2cde";
    private const string AdWithSlashInQueryUri = "https://ads.example/preroll.mp3?redirect=https://cdn.example/a";
    private const string FileUri = "https://files.example/beep.mp3";

    [Theory]
    // A radio track URI, whatever the media.
    [InlineData("96", "aac://http://host.example/aac/96", StationUri, true)]
    [InlineData("96", "aac://http://host.example/aac/96", null, true)]
    [InlineData("live.mp3", "x-rincon-mp3radio://host.example/live.mp3", "x-rincon-mp3radio://host.example/live.mp3", true)]
    [InlineData("host.example/live.mp3", "x-rincon-mp3radio://host.example/live.mp3", "x-rincon-mp3radio://host.example/live.mp3", true)]
    // A station's ad, an http(s) track while the media is the station.
    [InlineData("preroll.mp3", AdUri, StationUri, true)]
    [InlineData("preroll.mp3", AdUri, "x-rincon-mp3radio://host.example/live.mp3", true)]
    [InlineData("preroll.mp3", AdWithQueryUri, StationUri, true)]
    [InlineData("preroll.mp3?player=sonos&language=en%2cde", AdWithQueryUri, StationUri, true)]
    [InlineData("preroll.mp3?player=sonos&language=en,de", AdWithQueryUri, StationUri, true)]
    [InlineData("preroll.mp3", AdWithSlashInQueryUri, StationUri, true)]
    [InlineData("preroll.mp3?redirect=https://cdn.example/a", AdWithSlashInQueryUri, StationUri, true)]
    [InlineData("my ad.mp3", "https://ads.example/my%20ad.mp3", StationUri, true)]
    [InlineData("https://ads.example/preroll.mp3", AdUri, StationUri, true)]
    [InlineData("a", AdWithSlashInQueryUri, StationUri, false)]
    [InlineData("Advertisement", AdUri, StationUri, false)]
    // An http(s) file played once names itself by its file name, but never by its URL.
    [InlineData("beep.mp3", FileUri, FileUri, false)]
    [InlineData("beep.mp3", FileUri, QueueUri, false)]
    [InlineData("beep.mp3", FileUri, null, false)]
    [InlineData("https://files.example/beep.mp3", FileUri, FileUri, true)]
    [InlineData("files.example/beep.mp3", FileUri, FileUri, true)]
    // Any other track.
    [InlineData("song.mp3", "x-file-cifs://nas/music/song.mp3", QueueUri, false)]
    [InlineData("song.mp3", "x-file-cifs://nas/music/song.mp3", StationUri, false)]
    [InlineData("Song", null, StationUri, false)]
    public void WhenTrackTitleRepeatsTheTrackUri_ThenItIsNotATitle(string title, string? trackUri, string? mediaUri, bool expected)
    {
        // Act
        var isUri = SonosValues.IsTitleOfTrackUri(title, trackUri, mediaUri);

        // Assert
        Assert.Equal(expected, isUri);
    }
}
